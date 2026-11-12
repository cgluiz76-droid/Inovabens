using Inovabens.Core;
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

/// <summary>Fase 4 do CRM: prazo por etapa (alerta "etapa parada"), próximo passo sugerido e metas mensais.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class AutomacoesCrmTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private readonly string _sufixo = Guid.NewGuid().ToString("N")[..8];
    private readonly List<Guid> _etapasTeste = [];
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // as etapas de teste saem dos quadros dos outros testes
        await using (var db = await Fabrica.CreateDbContextAsync())
            await db.FunilEstagios.Where(e => _etapasTeste.Contains(e.Id)).ExecuteUpdateAsync(s => s.SetProperty(e => e.Ativo, false));
        _escopo.Dispose();
    }

    private IDbContextFactory<InovabensDb> Fabrica => S<IDbContextFactory<InovabensDb>>();

    private async Task<UsuarioAtual> Entrar(string email) => (await S<AuthService>().LoginAsync(email, BancoFixture.Senha, null)).Usuario!;

    /// <summary>Etapa aberta nova (com prazo) no funil, criada pela tela de configurações.</summary>
    private async Task<FunilEstagio> EtapaComPrazo(UsuarioAtual admin, Funil funil, int prazo)
    {
        var nome = $"Prazo {_sufixo} {_etapasTeste.Count}";
        await S<CrmService>().CriarEstagioAsync(admin, new EstagioForm { Funil = funil, Nome = nome, Ordem = 50, PrazoDias = prazo });
        await using var db = await Fabrica.CreateDbContextAsync();
        var e = await db.FunilEstagios.AsNoTracking().SingleAsync(x => x.Funil == funil && x.Nome == nome);
        _etapasTeste.Add(e.Id);
        return e;
    }

    private async Task<List<Alerta>> AlertasEtapa(Guid? lead = null, Guid? processo = null)
    {
        await using var db = await Fabrica.CreateDbContextAsync();
        return await db.Alertas.AsNoTracking()
            .Where(a => a.Tipo == TipoAlerta.ETAPA_PARADA && (lead == null || a.LeadId == lead) && (processo == null || a.ProcessoId == processo))
            .ToListAsync();
    }

    [Fact]
    public void Inicio_sugerido_cai_as_9h_ou_na_proxima_hora_de_hoje()
    {
        var manha = new DateTime(2026, 11, 12, 7, 30, 0);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 0, 0), ProximoPassoForm.InicioEmDias(0, manha));
        Assert.Equal(new DateTime(2026, 11, 14, 9, 0, 0), ProximoPassoForm.InicioEmDias(2, manha));
        var tarde = new DateTime(2026, 11, 12, 14, 10, 0);
        Assert.Equal(new DateTime(2026, 11, 12, 15, 0, 0), ProximoPassoForm.InicioEmDias(0, tarde));
        var noite = new DateTime(2026, 11, 12, 20, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 13, 9, 0, 0), ProximoPassoForm.InicioEmDias(0, noite));
    }

    [Fact]
    public async Task Etapas_padrao_tem_proximo_passo_sugerido_e_o_form_vem_preenchido()
    {
        var etapas = await S<CrmService>().EstagiosAsync(Funil.VENDA);
        var contato = etapas.Single(e => e.Nome == "Contato Realizado");
        Assert.Equal(TipoCompromisso.VISITA, contato.SugestaoTipo);
        Assert.Equal("Agendar visita ao imóvel", contato.SugestaoTitulo);
        Assert.Null(etapas.Single(e => e.Nome == "Perdido").SugestaoTitulo);

        var f = new ProximoPassoForm();
        f.Sugerir(contato, TipoCompromisso.LIGACAO);
        Assert.Equal(TipoCompromisso.VISITA, f.Tipo);
        Assert.Equal("Agendar visita ao imóvel", f.Titulo);
        Assert.Equal(ProximoPassoForm.InicioEmDias(2).Date, f.Inicio!.Value.Date);

        var semSugestao = new FunilEstagio { Nome = "X", Tipo = TipoEstagio.ABERTO };
        f.Sugerir(semSugestao, TipoCompromisso.TAREFA);
        Assert.Equal(TipoCompromisso.TAREFA, f.Tipo);
        Assert.Null(f.Titulo);
    }

    [Fact]
    public async Task Configuracao_da_etapa_grava_prazo_e_sugestao()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var crm = S<CrmService>();
        var e = await EtapaComPrazo(admin, Funil.CAPTACAO, 3);
        var f = EstagioForm.De(e);
        (f.SugestaoTitulo, f.SugestaoTipo, f.SugestaoDias, f.PrazoDias) = ("  Ligar de novo  ", TipoCompromisso.MENSAGEM, 4, 10);
        await crm.AtualizarEstagioAsync(admin, e.Id, f);
        var salvo = (await crm.EstagiosAsync(Funil.CAPTACAO)).Single(x => x.Id == e.Id);
        Assert.Equal(("Ligar de novo", TipoCompromisso.MENSAGEM, 4, 10), (salvo.SugestaoTitulo, salvo.SugestaoTipo!.Value, salvo.SugestaoDias!.Value, salvo.PrazoDias!.Value));

        // sem título, a sugestão some inteira; prazo vazio = sem prazo
        f = EstagioForm.De(salvo);
        (f.SugestaoTitulo, f.PrazoDias) = ("", null);
        await crm.AtualizarEstagioAsync(admin, e.Id, f);
        salvo = (await crm.EstagiosAsync(Funil.CAPTACAO)).Single(x => x.Id == e.Id);
        Assert.Null(salvo.SugestaoTitulo);
        Assert.Null(salvo.SugestaoTipo);
        Assert.Null(salvo.PrazoDias);

        await Assert.ThrowsAsync<RegraNegocioException>(() => crm.AtualizarEstagioAsync(admin, e.Id, new EstagioForm { Funil = Funil.CAPTACAO, Nome = e.Nome, PrazoDias = 0 }));
    }

    [Fact]
    public async Task Lead_parado_alem_do_prazo_gera_um_alerta_que_some_ao_mudar_de_etapa()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var crm = S<CrmService>();
        var prazos = S<PrazosService>();
        var etapa = await EtapaComPrazo(admin, Funil.CAPTACAO, 2);
        var lead = await crm.CriarAsync(admin, new LeadForm { NovoNome = "Parado " + _sufixo, Finalidade = FinalidadeLead.CAPTACAO, ResponsavelId = admin.Id });
        await using (var db = await Fabrica.CreateDbContextAsync())
        {
            await db.Leads.Where(l => l.Id == lead).ExecuteUpdateAsync(s => s.SetProperty(l => l.EstagioId, etapa.Id));
            db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = lead, EstagioNovoId = etapa.Id, UsuarioId = admin.Id, CriadoEm = DateTime.UtcNow.AddDays(-5) });
            await db.SaveChangesAsync();
        }

        await prazos.VerificarAsync();
        var a = Assert.Single(await AlertasEtapa(lead));
        Assert.Equal(admin.Id, a.UsuarioId);
        Assert.Null(a.ResolvidoEm);
        Assert.Contains("Parado " + _sufixo, a.Mensagem);
        Assert.Contains("prazo: 2 dias", a.Mensagem);

        await prazos.VerificarAsync(); // não duplica
        Assert.Single(await AlertasEtapa(lead));

        var cartao = (await crm.PipelineAsync(admin, Funil.CAPTACAO)).Single(c => c.Estagio.Id == etapa.Id).Leads.Single(l => l.Id == lead);
        Assert.True(etapa.Estourou(cartao.DiasNaFase));

        var primeira = (await crm.EstagiosAsync(Funil.CAPTACAO)).OrderBy(e => e.Ordem).First();
        await crm.MoverAsync(admin, lead, primeira.Id, proximo: new ProximoPassoForm { Pular = true, MotivoPular = "Teste de prazo" });
        Assert.NotNull(Assert.Single(await AlertasEtapa(lead)).ResolvidoEm);
    }

    [Fact]
    public async Task Processo_parado_avisa_o_responsavel_e_o_alerta_aponta_para_o_funil()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var processos = S<ProcessosService>();
        var etapa = await EtapaComPrazo(admin, Funil.ADMINISTRATIVO, 1);
        var imovel = await S<ImoveisService>().CriarAsync(admin, new ImovelForm { NomeDrive = "Prazo " + _sufixo, Titulo = "Casa com prazo", Tipo = "casa" });
        var id = await processos.CriarAsync(admin, new ProcessoForm { Funil = Funil.ADMINISTRATIVO, ImovelId = imovel, EstagioId = etapa.Id, ResponsavelId = admin.Id });
        await using (var db = await Fabrica.CreateDbContextAsync())
            await db.ProcessoHistoricos.Where(h => h.ProcessoId == id).ExecuteUpdateAsync(s => s.SetProperty(h => h.CriadoEm, DateTime.UtcNow.AddDays(-3)));

        await S<PrazosService>().VerificarAsync();
        var a = Assert.Single(await AlertasEtapa(processo: id));
        Assert.Equal(admin.Id, a.UsuarioId);
        var item = (await S<AlertasService>().ListarAsync(admin, false)).Single(x => x.Dados.Id == a.Id);
        Assert.Equal(Funil.ADMINISTRATIVO, item.FunilProcesso);
        Assert.Equal(imovel, item.ImovelId);

        // aumentar o prazo resolve na próxima verificação
        var f = EstagioForm.De(etapa);
        f.PrazoDias = 30;
        await S<CrmService>().AtualizarEstagioAsync(admin, etapa.Id, f);
        await S<PrazosService>().VerificarAsync();
        Assert.NotNull(Assert.Single(await AlertasEtapa(processo: id)).ResolvidoEm);
    }

    [Fact]
    public async Task Metas_mostram_o_realizado_do_mes_e_so_o_admin_define()
    {
        var admin = await Entrar("admin@inovabens.com.br");
        var carla = await Entrar("carla@inovabens.com.br");
        var metas = S<MetasService>();
        var mes = MetasService.MesDe(DatasBR.Hoje());

        await Assert.ThrowsAsync<AcessoNegadoException>(() => metas.SalvarAsync(carla, new MetaForm { UsuarioId = carla.Id, Atendimentos = 5 }));
        await metas.SalvarAsync(admin, new MetaForm { UsuarioId = carla.Id, Mes = mes.AddDays(10), Atendimentos = 40, Propostas = 8, Fechamentos = 2 });
        await metas.SalvarAsync(admin, new MetaForm { UsuarioId = carla.Id, Mes = mes, Atendimentos = 50, Propostas = 8, Fechamentos = 2 }); // altera, não duplica

        var antes = (await metas.PainelAsync(admin, mes)).Single(l => l.UsuarioId == carla.Id);
        Assert.Equal((50, 8, 2), (antes.MetaAtendimentos, antes.MetaPropostas, antes.MetaFechamentos));

        var crm = S<CrmService>();
        var lead = await crm.CriarAsync(carla, new LeadForm { NovoNome = "Meta " + _sufixo });
        await crm.CriarInteracaoAsync(carla, lead, new InteracaoForm { Resumo = "Primeiro contato", Pular = true, MotivoPular = "Cliente vai retornar" });
        await crm.CriarInteracaoAsync(carla, lead, new InteracaoForm { Resumo = "Segundo contato", Pular = true, MotivoPular = "Cliente vai retornar" });
        var ganho = (await crm.EstagiosAsync(Funil.VENDA)).Single(e => e.Tipo == TipoEstagio.GANHO);
        await crm.MoverAsync(carla, lead, ganho.Id, proximo: new ProximoPassoForm { Pular = true, MotivoPular = "Fechado" });

        var depois = Assert.Single(await metas.PainelAsync(carla, mes)); // corretor vê só a própria linha
        Assert.Equal(carla.Id, depois.UsuarioId);
        Assert.Equal(antes.Atendimentos + 1, depois.Atendimentos); // o mesmo lead conta uma vez
        Assert.Equal(antes.Fechamentos + 1, depois.Fechamentos);
        Assert.Equal(50, LinhaMeta.Pct(25, 50));
        Assert.Null(LinhaMeta.Pct(3, 0));

        var id = (await metas.PainelAsync(admin, mes)).Single(l => l.UsuarioId == carla.Id).MetaId!.Value;
        await metas.ExcluirAsync(admin, id);
        Assert.False((await metas.PainelAsync(admin, mes)).Single(l => l.UsuarioId == carla.Id).TemMeta);
    }
}
