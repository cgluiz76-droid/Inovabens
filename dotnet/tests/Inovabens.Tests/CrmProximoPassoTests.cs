using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Crm;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Regras do formulário de próximo passo e formatações do cartão (sem banco).</summary>
public class ProximoPassoRegrasTests
{
    [Fact]
    public void Proximo_passo_exige_titulo_e_data_futura_ou_motivo_para_pular()
    {
        var erro = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ProximoPassoForm()));
        Assert.Contains(nameof(ProximoPassoForm.Titulo), erro.Erros.Keys);
        Assert.Contains(nameof(ProximoPassoForm.Inicio), erro.Erros.Keys);

        erro = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(
            new ProximoPassoForm { Titulo = "Ligar", Inicio = DatasBR.Agora().AddDays(-1) }));
        Assert.Contains("já passou", erro.Erros[nameof(ProximoPassoForm.Inicio)][0]);

        erro = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(new ProximoPassoForm { Pular = true, MotivoPular = " " }));
        Assert.Contains(nameof(ProximoPassoForm.MotivoPular), erro.Erros.Keys);

        Validacao.Validar(new ProximoPassoForm { Pular = true, MotivoPular = "Cliente viajando" });
        Validacao.Validar(new ProximoPassoForm { Titulo = "Ligar", Inicio = ProximoPassoForm.SugestaoInicio() });
    }

    [Fact]
    public void Interacao_tambem_exige_o_proximo_passo()
    {
        var f = new InteracaoForm { Resumo = "Conversamos" };
        Assert.NotNull(f.Inicio); // já vem com a sugestão (amanhã 9h)
        var erro = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(f));
        Assert.Contains(nameof(ProximoPassoForm.Titulo), erro.Erros.Keys);
    }

    [Fact]
    public void Situacao_do_proximo_passo()
    {
        Assert.Equal(SituacaoProximoPasso.Nenhum, LeadCartao.SituacaoDe(null));
        Assert.Equal(SituacaoProximoPasso.Atrasado, LeadCartao.SituacaoDe(DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(SituacaoProximoPasso.Agendado, LeadCartao.SituacaoDe(DateTime.UtcNow.AddDays(2)));
        var fimDoDia = DatasBR.ParaUtc(DatasBR.Agora().Date.AddHours(23).AddMinutes(59));
        if (fimDoDia > DateTime.UtcNow.AddMinutes(1))
            Assert.Equal(SituacaoProximoPasso.Hoje, LeadCartao.SituacaoDe(fimDoDia));
    }

    [Fact]
    public void Dados_alterados_compara_so_os_campos_do_lead_e_ignora_mudanca_de_estagio()
    {
        Assert.Equal(["orçamento máximo", "observações"], CrmService.CamposAlterados(
            """{"origem":"Site","orcamento_max":100,"observacoes":null,"atualizado_em":"a"}""",
            """{"origem":"Site","orcamento_max":200,"observacoes":"x","atualizado_em":"b"}"""));
        Assert.Empty(CrmService.CamposAlterados("""{"estagio":"A"}""", """{"estagio":"B","motivo_perda":null}"""));
        Assert.Empty(CrmService.CamposAlterados(null, "{}"));
        Assert.Empty(CrmService.CamposAlterados("não é json", "{}"));
    }

    [Fact]
    public void Link_do_whatsapp_poe_o_codigo_do_pais()
    {
        Assert.Equal("https://wa.me/5511988887777", Texto.LinkWhatsapp("(11) 98888-7777"));
        Assert.Equal("https://wa.me/5511988887777", Texto.LinkWhatsapp("+55 11 98888-7777"));
        Assert.Null(Texto.LinkWhatsapp("1234"));
        Assert.Null(Texto.LinkWhatsapp(null));
    }

    [Theory]
    [InlineData(950, "R$ 950")]
    [InlineData(480_000, "R$ 480 mil")]
    [InlineData(1_250_000, "R$ 1,25 mi")]
    public void Moeda_compacta(decimal v, string esperado) =>
        Assert.Equal(esperado, Texto.MoedaCompacta(v).Replace(' ', ' '));
}

/// <summary>Próximo passo no CRM: agenda, conclusão de atividades, Perdido/Ganho, filtro por imóvel e interessados.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class CrmProximoPassoTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();
    private readonly string sufixo = Guid.NewGuid().ToString("N")[..6];
    private UsuarioAtual admin = default!, corretor = default!;

    public async Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope(); // provider compartilhado (limite de 20 do EF)
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var auth = S<AuthService>();
        admin = (await auth.LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        corretor = (await auth.LoginAsync("carla@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    private Task<Guid> Imovel(string titulo, decimal? preco = null, UsuarioAtual? dono = null) =>
        S<ImoveisService>().CriarAsync(dono ?? admin, new ImovelForm
        {
            NomeDrive = Guid.NewGuid().ToString(), Titulo = $"{titulo} {sufixo}", Tipo = "casa", PrecoVenda = preco,
            CorretorResponsavelId = (dono ?? admin).Id,
        });

    private static DateTime Amanha(int hora = 10) => DatasBR.Agora().Date.AddDays(1).AddHours(hora);

    private async Task<List<Compromisso>> Compromissos(Guid leadId)
    {
        await using var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync();
        return await db.Compromissos.AsNoTracking().Where(c => c.LeadId == leadId).OrderBy(c => c.CriadoEm).ToListAsync();
    }

    private async Task<LeadCartao> Cartao(Guid leadId, Guid? imovel = null) =>
        (await S<CrmService>().PipelineAsync(admin, Funil.VENDA, imovel)).SelectMany(c => c.Leads).Single(l => l.Id == leadId);

    [Fact]
    public async Task Acao_agenda_o_proximo_passo_ou_guarda_o_motivo_e_concluir_registra_o_resultado()
    {
        var crm = S<CrmService>();
        var id = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Próximo Passo {sufixo}", NovoTelefone = "11988887777" });
        Assert.Equal(SituacaoProximoPasso.Nenhum, (await Cartao(id)).Situacao);

        // sem próximo passo e sem motivo: recusado
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.CriarInteracaoAsync(admin, id, new InteracaoForm { Resumo = "Primeiro contato" }));

        // pular com motivo: guarda o motivo, nada na agenda
        await crm.CriarInteracaoAsync(admin, id, new InteracaoForm { Resumo = "Primeiro contato", Pular = true, MotivoPular = "Cliente vai retornar" });
        Assert.Equal("Cliente vai retornar", Assert.Single(await crm.InteracoesAsync(admin, id)).Dados.MotivoSemProximo);
        Assert.Empty(await Compromissos(id));

        // agendar: lead sem imóvel também tem compromisso (imóvel vazio), visível na agenda com o nome do cliente
        await crm.CriarInteracaoAsync(admin, id, new InteracaoForm
        {
            Canal = CanalInteracao.TELEFONE, Resumo = "Pediu opções", Tipo = TipoCompromisso.LIGACAO, Titulo = "Ligar com opções", Inicio = Amanha(),
        });
        var c1 = Assert.Single(await Compromissos(id));
        Assert.Null(c1.ImovelId);
        Assert.Equal(TipoCompromisso.LIGACAO, c1.Tipo);
        Assert.Equal(30, (c1.FimEm - c1.InicioEm).TotalMinutes);
        Assert.Equal(c1.Id, (await crm.InteracoesAsync(admin, id))[0].Dados.ProximoCompromissoId);
        var cartao = await Cartao(id);
        Assert.Equal(SituacaoProximoPasso.Agendado, cartao.Situacao);
        Assert.Equal("Ligar com opções", cartao.ProximoTitulo);
        var naAgenda = await S<AgendaService>().ObterAsync(admin, c1.Id);
        Assert.Null(naAgenda.ImovelCodigo);
        Assert.Equal($"Próximo Passo {sufixo}", naAgenda.LeadNome);
        var atividade = Assert.Single(await crm.AtividadesAsync(admin), a => a.Id == c1.Id);
        Assert.Equal("11988887777", atividade.Telefone);

        // com um único imóvel de interesse, a atividade fica ligada a ele
        var im = await Imovel("Interesse único");
        await crm.DefinirInteresseAsync(admin, id, [im]);

        // concluir a atividade: o resumo é o resultado e o próximo passo é obrigatório de novo
        await crm.CriarInteracaoAsync(admin, id, new InteracaoForm
        {
            Canal = CanalInteracao.TELEFONE, Resumo = "Gostou da casa", ConcluirCompromissoId = c1.Id,
            Tipo = TipoCompromisso.VISITA, Titulo = "Visita", Inicio = Amanha(15), DuracaoMin = 60,
        });
        var lista = await Compromissos(id);
        Assert.Equal(StatusCompromisso.CONCLUIDO, lista[0].Status);
        Assert.Equal(im, lista[1].ImovelId);
        Assert.Equal(StatusCompromisso.PENDENTE, lista[1].Status);
        var ultima = (await crm.InteracoesAsync(admin, id))[0].Dados;
        Assert.Equal(c1.Id, ultima.CompromissoConcluidoId);
        Assert.Equal(lista[1].Id, ultima.ProximoCompromissoId);

        // não conclui duas vezes a mesma atividade
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.CriarInteracaoAsync(admin, id, new InteracaoForm
        {
            Resumo = "De novo", ConcluirCompromissoId = c1.Id, Pular = true, MotivoPular = "teste",
        }));

        // reagendar (adiar) a atividade
        await S<AgendaService>().ReagendarAsync(admin, lista[1].Id, Amanha(16), Amanha(17));
        Assert.Equal(DatasBR.ParaUtc(Amanha(16)), (await Compromissos(id))[1].InicioEm);

        // "+" do cartão: agenda sem poder pular
        await crm.AgendarAsync(admin, id, new ProximoPassoForm { Titulo = "Mandar fotos", Tipo = TipoCompromisso.MENSAGEM, Inicio = Amanha(9) });
        Assert.Equal("Mandar fotos", (await Cartao(id)).ProximoTitulo); // o mais próximo aparece no cartão
        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.AgendarAsync(admin, id, new ProximoPassoForm { Pular = true, MotivoPular = "não" }));
    }

    [Fact]
    public async Task Perdido_cancela_a_agenda_e_ganho_continua_com_proximos_passos()
    {
        var crm = S<CrmService>();
        var estagios = await crm.EstagiosAsync(Funil.VENDA);
        var abertos = estagios.Where(e => e.Ativo && e.Tipo == TipoEstagio.ABERTO).OrderBy(e => e.Ordem).ToList();
        var ganho = estagios.First(e => e.Ativo && e.Tipo == TipoEstagio.GANHO);
        var perdido = estagios.First(e => e.Ativo && e.Tipo == TipoEstagio.PERDIDO);

        // mudar de estágio com próximo passo registra a ação "mudou de estágio"
        var a = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Vai Perder {sufixo}" });
        await crm.MoverAsync(admin, a, abertos[1].Id, proximo: new ProximoPassoForm { Titulo = "Ligar", Inicio = Amanha() });
        var acao = Assert.Single(await crm.InteracoesAsync(admin, a)).Dados;
        Assert.Equal(CanalInteracao.OUTRO, acao.Canal);
        Assert.Contains(abertos[1].Nome, acao.Resumo);
        Assert.NotNull(acao.ProximoCompromissoId);
        await crm.AgendarAsync(admin, a, new ProximoPassoForm { Titulo = "Visita", Tipo = TipoCompromisso.VISITA, Inicio = Amanha(14) });

        // perdido: motivo obrigatório, sem próximo passo (ignorado se vier) e as pendentes são canceladas
        await crm.MoverAsync(admin, a, perdido.Id, "Desistiu", proximo: new ProximoPassoForm { Titulo = "Ignorar", Inicio = Amanha() });
        Assert.All(await Compromissos(a), c => Assert.Equal(StatusCompromisso.CANCELADO, c.Status));
        Assert.Equal(2, (await Compromissos(a)).Count);
        Assert.Null((await crm.ObterAsync(admin, a)).Dados.ProximaAcaoEm);
        Assert.Equal(SituacaoProximoPasso.Nenhum, (await Cartao(a)).Situacao);

        // ganho: segue com passos como assinatura do contrato
        var g = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Vai Ganhar {sufixo}" });
        await crm.MoverAsync(admin, g, ganho.Id, proximo: new ProximoPassoForm { Titulo = "Assinatura do contrato", Tipo = TipoCompromisso.REUNIAO, Inicio = Amanha() });
        var c = Assert.Single(await Compromissos(g));
        Assert.Equal(StatusCompromisso.PENDENTE, c.Status);
        Assert.Equal("Assinatura do contrato", c.Titulo);
    }

    [Fact]
    public async Task Filtro_por_imovel_valor_do_cartao_e_aba_interessados()
    {
        var crm = S<CrmService>();
        var casa = await Imovel("Casa Filtro", 480_000);
        var apto = await Imovel("Apto Filtro", 650_000);
        var l1 = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Interessado Casa {sufixo}", ImoveisInteresse = [casa] });
        var l2 = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Interessado Dois {sufixo}", ImoveisInteresse = [casa, apto] });
        var l3 = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Sem Casa {sufixo}", ImoveisInteresse = [apto] });
        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
        {
            // l3 citou a casa no WhatsApp (sugerido, ainda sem vínculo)
            await db.Leads.Where(l => l.Id == l3).ExecuteUpdateAsync(s => s.SetProperty(l => l.ImovelSugeridoId, casa));
        }

        var colunas = await crm.PipelineAsync(admin, Funil.VENDA, casa);
        var ids = colunas.SelectMany(c => c.Leads).Select(l => l.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Contains(l1, ids);
        Assert.Contains(l3, ids);
        Assert.Equal(480_000 + 650_000 + 650_000, colunas.Sum(c => c.ValorTotal));

        var c1 = await Cartao(l1, casa);
        Assert.Equal(480_000, c1.Valor);
        Assert.StartsWith("IMV-", c1.ImovelCodigo);
        Assert.Equal(0, c1.OutrosImoveis);
        var c2 = await Cartao(l2, casa);
        Assert.Equal(650_000, c2.Valor); // o maior dos imóveis de interesse
        Assert.Equal(1, c2.OutrosImoveis);
        Assert.Equal(0, c2.DiasNaFase);

        Assert.Equal(2, (await crm.PipelineAsync(admin, Funil.VENDA, apto)).SelectMany(c => c.Leads).Count());

        var interessados = await crm.InteressadosAsync(admin, casa);
        Assert.Equal(3, interessados.Count);
        Assert.True(interessados.Single(i => i.LeadId == l3).SoSugerido);
        Assert.False(interessados.Single(i => i.LeadId == l1).SoSugerido);

        // a corretora não vê a aba de imóvel alheio
        await Assert.ThrowsAsync<AcessoNegadoException>(() => crm.InteressadosAsync(corretor, casa));
    }

    [Fact]
    public async Task Linha_do_tempo_junta_acoes_atividades_estagios_e_dados()
    {
        var crm = S<CrmService>();
        var estagios = (await crm.EstagiosAsync(Funil.VENDA)).Where(e => e.Ativo).OrderBy(e => e.Ordem).ToList();
        var perdido = estagios.First(e => e.Tipo == TipoEstagio.PERDIDO);
        var id = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Linha do Tempo {sufixo}" });
        await crm.CriarInteracaoAsync(admin, id, new InteracaoForm
        {
            Canal = CanalInteracao.TELEFONE, Resumo = "Atendeu e pediu fotos", Titulo = "Mandar fotos", Tipo = TipoCompromisso.MENSAGEM, Inicio = Amanha(),
        });
        await crm.AgendarAsync(admin, id, new ProximoPassoForm { Titulo = "Ligar de novo", Inicio = Amanha(15) });
        await crm.AtualizarAsync(admin, id, new LeadForm { OrcamentoMax = 500_000, Observacoes = "Quer garagem" });
        await crm.MoverAsync(admin, id, perdido.Id, "Comprou em outro lugar");

        var eventos = await crm.LinhaDoTempoAsync(admin, id);
        var acao = Assert.Single(eventos, e => e.Tipo == TipoEventoLead.Acao);
        Assert.Equal("Atendeu e pediu fotos", acao.Titulo);
        Assert.StartsWith("Próximo passo: Mandar fotos", acao.Detalhe);
        Assert.Equal("📞", acao.Icone);
        // agendada pela ação não se repete; a do "+" aparece; as duas foram canceladas ao perder
        Assert.Single(eventos, e => e.Titulo == "Atividade agendada: Ligar de novo");
        Assert.DoesNotContain(eventos, e => e.Titulo == "Atividade agendada: Mandar fotos");
        Assert.Equal(2, eventos.Count(e => e.Titulo.StartsWith("Atividade cancelada")));
        Assert.Contains(eventos, e => e.Tipo == TipoEventoLead.Estagio && e.Titulo.EndsWith($"→ {perdido.Nome}"));
        Assert.Contains(eventos, e => e.Tipo == TipoEventoLead.Estagio && e.Titulo.StartsWith("Lead criado em"));
        var dados = Assert.Single(eventos, e => e.Tipo == TipoEventoLead.Dados);
        Assert.Contains("orçamento máximo", dados.Detalhe);
        Assert.Contains("observações", dados.Detalhe);
        Assert.Equal(eventos.OrderByDescending(e => e.Em).Select(e => e.Titulo), eventos.Select(e => e.Titulo));

        // atividades do lead: as encerradas também aparecem
        Assert.Equal(2, (await crm.AtividadesDoLeadAsync(admin, id)).Count);
        Assert.Empty(await crm.PendentesDoLeadAsync(admin, id));
        await Assert.ThrowsAnyAsync<Exception>(() => crm.LinhaDoTempoAsync(corretor, id)); // lead de outro responsável
    }

    [Fact]
    public async Task Minhas_atividades_mostra_so_as_do_usuario_e_a_equipe_para_o_gestor()
    {
        var crm = S<CrmService>();
        var daCarla = await Imovel("Imóvel da Carla", dono: corretor);
        var lead = await crm.CriarAsync(corretor, new LeadForm { NovoNome = $"Lead da Carla {sufixo}", ImoveisInteresse = [daCarla] });
        var id = await crm.AgendarAsync(corretor, lead, new ProximoPassoForm { Titulo = "Ligar Carla", Inicio = Amanha() });

        Assert.Contains(await crm.AtividadesAsync(corretor), a => a.Id == id && a.ImovelId == daCarla && a.LeadId == lead);
        Assert.DoesNotContain(await crm.AtividadesAsync(admin), a => a.Id == id);
        Assert.Contains(await crm.AtividadesAsync(admin, todas: true), a => a.Id == id);
        // corretor não vê as dos outros nem pedindo "todas"
        var doAdmin = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Lead do Admin {sufixo}" });
        var outra = await crm.AgendarAsync(admin, doAdmin, new ProximoPassoForm { Titulo = "Ligar Admin", Inicio = Amanha() });
        Assert.DoesNotContain(await crm.AtividadesAsync(corretor, todas: true), a => a.Id == outra);
    }
}
