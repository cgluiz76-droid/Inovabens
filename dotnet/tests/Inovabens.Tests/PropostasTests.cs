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

public class PropostasRegrasTests
{
    [Theory]
    [InlineData(450_000, 500_000, -10)]
    [InlineData(520_000, 500_000, 4)]
    [InlineData(2_800, 3_000, -6.7)]
    public void Diferenca_para_o_anunciado(decimal total, decimal anunciado, decimal pct) =>
        Assert.Equal(pct, PropostasService.DiferencaPct(total, anunciado));

    [Fact]
    public void Sem_preco_anunciado_nao_ha_diferenca() => Assert.Null(PropostasService.DiferencaPct(100, null));

    [Fact]
    public void Contraproposta_copia_os_valores_e_inverte_o_autor()
    {
        var p = new Proposta
        {
            ImovelId = Guid.NewGuid(), Autor = AutorProposta.CLIENTE, Condicoes = "Entrega em 60 dias",
            Itens = [new PropostaItem { Ordem = 1, Tipo = TipoItemProposta.FINANCIAMENTO, Valor = 300, Instituicao = "Caixa" },
                     new PropostaItem { Ordem = 0, Tipo = TipoItemProposta.ENTRADA, Valor = 100 }],
        };
        var f = PropostaForm.ContrapropostaDe(p);
        Assert.Equal(AutorProposta.PROPRIETARIO, f.Autor);
        Assert.Equal([TipoItemProposta.ENTRADA, TipoItemProposta.FINANCIAMENTO], f.Itens.Select(i => i.Tipo));
        Assert.Equal(400, f.Total(FinalidadeLead.COMPRA));
        Assert.Equal("Caixa", f.Itens[1].Instituicao);
    }

    [Fact]
    public void Proposta_tambem_pede_o_proximo_passo()
    {
        var f = new PropostaForm { ImovelId = Guid.NewGuid() };
        var erro = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(f));
        Assert.Contains(nameof(ProximoPassoForm.Titulo), erro.Erros.Keys);
        f.ValidadeEm = DatasBR.Hoje().AddDays(-1);
        f.Pular = true;
        f.MotivoPular = "Aguardando o proprietário";
        erro = Assert.Throws<RegraNegocioException>(() => Validacao.Validar(f));
        Assert.Contains(nameof(PropostaForm.ValidadeEm), erro.Erros.Keys);
    }
}

[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class PropostasTests : IAsyncLifetime
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

    private Task<Guid> Imovel(string titulo, UsuarioAtual dono, decimal? venda = null, decimal? locacao = null) =>
        S<ImoveisService>().CriarAsync(dono, new ImovelForm
        {
            NomeDrive = Guid.NewGuid().ToString(), Titulo = $"{titulo} {sufixo}", Tipo = "casa", PrecoVenda = venda, PrecoLocacao = locacao,
            CorretorResponsavelId = dono.Id,
        });

    private static PropostaForm Venda(Guid imovel, params (TipoItemProposta Tipo, decimal Valor)[] itens) => new()
    {
        ImovelId = imovel, Titulo = "Levar ao proprietário", Inicio = DatasBR.Agora().Date.AddDays(1).AddHours(10),
        Itens = [.. itens.Select(i => new PropostaItemForm { Tipo = i.Tipo, Valor = i.Valor })],
    };

    private static ProximoPassoForm Passo(string titulo) => new() { Titulo = titulo, Inicio = DatasBR.Agora().Date.AddDays(2).AddHours(10) };

    [Fact]
    public async Task Proposta_contraproposta_e_aceite_pelo_administrador_leva_para_fechamento()
    {
        var crm = S<CrmService>();
        var propostas = S<PropostasService>();
        var casa = await Imovel("Casa Proposta", corretor, venda: 500_000);
        var lead = await crm.CriarAsync(corretor, new LeadForm { NovoNome = $"Comprador {sufixo}" });

        // venda sem forma de pagamento não grava
        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.CriarAsync(corretor, lead, Venda(casa)));

        var f = Venda(casa, (TipoItemProposta.ENTRADA, 100_000), (TipoItemProposta.FINANCIAMENTO, 300_000), (TipoItemProposta.PERMUTA_VEICULO, 50_000));
        f.Itens[1].Instituicao = "Caixa";
        f.Itens[2].Descricao = "Onix 2022 placa ABC1D23";
        var p1 = await propostas.CriarAsync(corretor, lead, f);
        var d1 = await propostas.ObterAsync(corretor, p1);
        Assert.Equal(450_000, d1.Dados.ValorTotal);
        Assert.Equal(500_000, d1.Dados.ValorAnunciado);
        Assert.Equal(-10, d1.DiferencaPct);
        Assert.Equal(1, d1.Dados.Numero);
        Assert.Equal([TipoItemProposta.ENTRADA, TipoItemProposta.FINANCIAMENTO, TipoItemProposta.PERMUTA_VEICULO], d1.Dados.Itens.Select(i => i.Tipo));
        Assert.Equal("Caixa", d1.Dados.Itens[1].Instituicao);
        // o imóvel entrou nos interesses e a ação ficou registrada com o próximo passo
        Assert.Equal(casa, Assert.Single((await crm.ObterAsync(corretor, lead)).ImoveisInteresse).Id);
        var acao = (await crm.InteracoesAsync(corretor, lead))[0].Dados;
        Assert.Equal(p1, acao.PropostaId);
        Assert.NotNull(acao.ProximoCompromissoId);
        Assert.Contains("-10% do anunciado", acao.Resumo);
        var cartao = (await crm.PipelineAsync(corretor, Funil.VENDA)).SelectMany(c => c.Leads).Single(l => l.Id == lead);
        Assert.Equal(450_000, cartao.ValorAtual);
        Assert.Equal(-10, cartao.DiferencaPct);
        Assert.Equal(StatusProposta.EM_ANALISE, cartao.StatusProposta);

        // contraproposta do proprietário substitui a anterior
        var contra = PropostaForm.ContrapropostaDe(d1.Dados);
        contra.Itens[0].Valor = 130_000;
        contra.Limpar();
        contra.Titulo = "Apresentar contraproposta";
        var p2 = await propostas.CriarAsync(corretor, lead, contra);
        var lista = await propostas.DoLeadAsync(corretor, lead);
        Assert.Equal([p2, p1], lista.Select(x => x.Dados.Id));
        Assert.Equal(StatusProposta.SUBSTITUIDA, lista[1].Dados.Status);
        Assert.Equal(2, lista[0].Dados.Numero);
        Assert.Equal(p1, lista[0].Dados.AnteriorId);
        Assert.Equal(AutorProposta.PROPRIETARIO, lista[0].Dados.Autor);
        Assert.Equal(480_000, lista[0].Dados.ValorTotal);

        // só o administrador decide; recusa exige motivo
        Assert.False(corretor.Pode(Permissoes.CrmPropostasDecidir));
        Assert.True(admin.Pode(Permissoes.CrmPropostasDecidir));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => propostas.DecidirAsync(corretor, p2, true, null, Passo("Assinatura")));
        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.DecidirAsync(admin, p2, false, " ", Passo("Ligar")));
        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.DecidirAsync(admin, p1, true, null, Passo("Assinatura"))); // substituída

        await propostas.DecidirAsync(admin, p2, true, null, Passo("Assinatura do contrato"));
        var aceita = await propostas.ObterAsync(admin, p2);
        Assert.Equal(StatusProposta.ACEITA, aceita.Dados.Status);
        Assert.Equal("Administrador", aceita.DecididoPorNome);
        var dl = await crm.ObterAsync(admin, lead);
        Assert.Equal("Documentação/Fechamento", dl.Estagio.Nome);
        Assert.Contains(await crm.HistoricoAsync(admin, lead), h => h.EstagioNovo == "Documentação/Fechamento");
        Assert.Contains(await crm.PendentesDoLeadAsync(admin, lead), c => c.Titulo == "Assinatura do contrato");
        Assert.Contains(await crm.LinhaDoTempoAsync(admin, lead), e => e.Icone == "💰" && e.Titulo.Contains("aceita"));
        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.DecidirAsync(admin, p2, false, "mudou de ideia", Passo("x")));

        // aba Interessados mostra a proposta aceita
        var interessado = Assert.Single(await crm.InteressadosAsync(admin, casa));
        Assert.Equal(StatusProposta.ACEITA, interessado.StatusProposta);
        Assert.Equal(-4, interessado.DiferencaPct);
    }

    [Fact]
    public async Task Locacao_usa_aluguel_e_garantia_e_cancelar_exige_motivo()
    {
        var crm = S<CrmService>();
        var propostas = S<PropostasService>();
        var apto = await Imovel("Apto Aluguel", admin, locacao: 3_000);
        var lead = await crm.CriarAsync(admin, new LeadForm { NovoNome = $"Inquilino {sufixo}", Finalidade = FinalidadeLead.LOCACAO });

        var f = new PropostaForm { ImovelId = apto, Pular = true, MotivoPular = "Aguardar retorno do proprietário" };
        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.CriarAsync(admin, lead, f));
        f.Aluguel = 2_800;
        f.Garantia = GarantiaLocacao.SEGURO_FIANCA;
        f.PrazoMeses = 30;
        f.IndiceReajuste = "IGP-M";
        f.Itens = [new PropostaItemForm { Tipo = TipoItemProposta.ENTRADA, Valor = 1 }]; // ignorado na locação
        var id = await propostas.CriarAsync(admin, lead, f);
        var d = await propostas.ObterAsync(admin, id);
        Assert.Equal(2_800, d.Dados.ValorTotal);
        Assert.Equal(3_000, d.Dados.ValorAnunciado);
        Assert.Equal(-6.7m, d.DiferencaPct);
        Assert.Equal(GarantiaLocacao.SEGURO_FIANCA, d.Dados.Garantia);
        Assert.Empty(d.Dados.Itens);
        Assert.Equal("Aguardar retorno do proprietário", (await crm.InteracoesAsync(admin, lead))[0].Dados.MotivoSemProximo);

        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.CancelarAsync(admin, id, null, Passo("x")));
        await propostas.CancelarAsync(admin, id, "Cliente achou outro imóvel", new ProximoPassoForm { Pular = true, MotivoPular = "Vai procurar outro" });
        Assert.Equal(StatusProposta.CANCELADA, (await propostas.ObterAsync(admin, id)).Dados.Status);
        Assert.Null((await crm.PipelineAsync(admin, Funil.LOCACAO)).SelectMany(c => c.Leads).Single(l => l.Id == lead).ValorProposta);
    }

    [Fact]
    public async Task Estagio_de_fechamento_existe_antes_do_ganho_nos_dois_funis()
    {
        foreach (var funil in Enum.GetValues<Funil>())
        {
            var e = (await S<CrmService>().EstagiosAsync(funil)).OrderBy(x => x.Ordem).ToList();
            var fech = e.FindIndex(x => x.Nome == "Documentação/Fechamento");
            Assert.True(fech >= 0);
            Assert.Equal(TipoEstagio.GANHO, e[fech + 1].Tipo);
        }
    }

    [Fact]
    public async Task Corretor_nao_ve_proposta_de_lead_alheio()
    {
        var propostas = S<PropostasService>();
        var im = await Imovel("Imóvel Admin", admin, venda: 100_000);
        var lead = await S<CrmService>().CriarAsync(admin, new LeadForm { NovoNome = $"Do Admin {sufixo}" });
        var id = await propostas.CriarAsync(admin, lead, Venda(im, (TipoItemProposta.ENTRADA, 90_000)));
        await Assert.ThrowsAsync<NaoEncontradoException>(() => propostas.ObterAsync(corretor, id));
        await Assert.ThrowsAsync<NaoEncontradoException>(() => propostas.DoLeadAsync(corretor, lead));
        // corretor não faz proposta com imóvel alheio que não está no interesse do lead
        var deleLead = await S<CrmService>().CriarAsync(corretor, new LeadForm { NovoNome = $"Da Carla {sufixo}" });
        await Assert.ThrowsAsync<NaoEncontradoException>(() => propostas.CriarAsync(corretor, deleLead, Venda(im, (TipoItemProposta.ENTRADA, 90_000))));
    }

    [Fact]
    public async Task Proposta_vencida_expira_registra_no_lead_e_avisa_o_responsavel()
    {
        var propostas = S<PropostasService>();
        var casa = await Imovel("Casa Validade", corretor, venda: 300_000);
        var lead = await S<CrmService>().CriarAsync(corretor, new LeadForm { NovoNome = $"Validade {sufixo}", ResponsavelId = corretor.Id });
        var f = Venda(casa, (TipoItemProposta.ENTRADA, 280_000));
        f.ValidadeEm = DatasBR.Hoje();
        var id = await propostas.CriarAsync(corretor, lead, f);

        // ainda no dia da validade: não expira
        await propostas.ExpirarVencidasAsync();
        Assert.Equal(StatusProposta.EM_ANALISE, (await propostas.ObterAsync(corretor, id)).Dados.Status);

        await using (var db = await S<IDbContextFactory<InovabensDb>>().CreateDbContextAsync())
            await db.Propostas.Where(p => p.Id == id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ValidadeEm, DatasBR.Hoje().AddDays(-1)));
        Assert.True(await propostas.ExpirarVencidasAsync() >= 1);
        var d = await propostas.ObterAsync(corretor, id);
        Assert.Equal(StatusProposta.EXPIRADA, d.Dados.Status);
        Assert.StartsWith("Validade vencida em", d.Dados.MotivoDecisao);
        Assert.Contains("expirou", (await S<CrmService>().InteracoesAsync(corretor, lead))[0].Dados.Resumo);
        var alerta = Assert.Single((await S<AlertasService>().ListarAsync(corretor, false)).Where(a => a.LeadId == lead));
        Assert.Equal(TipoAlerta.PROPOSTA_EXPIRADA, alerta.Dados.Tipo);
        // a rotina é idempotente
        await propostas.ExpirarVencidasAsync();
        Assert.Single((await S<AlertasService>().ListarAsync(corretor, false)).Where(a => a.LeadId == lead));

        // nova proposta resolve o aviso
        await propostas.CriarAsync(corretor, lead, Venda(casa, (TipoItemProposta.ENTRADA, 290_000)));
        Assert.Empty((await S<AlertasService>().ListarAsync(corretor, false)).Where(a => a.LeadId == lead));
    }

    [Fact]
    public async Task Imovel_recebido_em_permuta_fica_ligado_a_proposta()
    {
        var propostas = S<PropostasService>();
        var casa = await Imovel("Casa Permuta", corretor, venda: 800_000);
        var lead = await S<CrmService>().CriarAsync(corretor, new LeadForm { NovoNome = $"Permutante {sufixo}" });
        var f = Venda(casa, (TipoItemProposta.ENTRADA, 300_000), (TipoItemProposta.PERMUTA_IMOVEL, 450_000));
        f.Itens[1].Descricao = "Apartamento 2 quartos no Centro";
        var id = await propostas.CriarAsync(corretor, lead, f);
        var item = (await propostas.ObterAsync(corretor, id)).Dados.Itens[1];

        var dados = await propostas.PermutaAsync(corretor, item.Id);
        Assert.Equal(lead, dados.LeadId);
        Assert.Equal(450_000, dados.Valor);
        Assert.Equal("Apartamento 2 quartos no Centro", dados.Descricao);
        // só a parte "permuta de imóvel" serve
        var entrada = (await propostas.ObterAsync(corretor, id)).Dados.Itens[0].Id;
        await Assert.ThrowsAsync<NaoEncontradoException>(() => propostas.PermutaAsync(corretor, entrada));

        var apto = await Imovel("Apto da permuta", corretor);
        await propostas.VincularPermutaAsync(corretor, item.Id, apto);
        Assert.Equal(apto, (await propostas.ObterAsync(corretor, id)).Dados.Itens[1].ImovelPermutaId);
        Assert.Contains("Imóvel da permuta", (await S<CrmService>().InteracoesAsync(corretor, lead))[0].Dados.Resumo);
        await Assert.ThrowsAsync<RegraNegocioException>(() => propostas.VincularPermutaAsync(corretor, item.Id, apto));
    }
}
