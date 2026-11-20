using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Inovabens.Web.Components.Financeiro;

namespace Inovabens.Tests;

/// <summary>
/// Fluxo do módulo financeiro contra PostgreSQL real (mesmos pré-requisitos de <see cref="ImoveisIntegracaoTests"/>,
/// mais as contas orçamentárias padrão e financeiro:ver/editar no perfil Corretor).
/// Reproduz o cenário do e2e do Node (agora com lançamentos financeiros e fechamento) e confere os mesmos números.
/// </summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class FinanceiroIntegracaoTests
{
    private static string Url => BancoFixture.Url;
    private static DateOnly D(int a, int m, int d) => new(a, m, d);

    [Fact]
    public async Task Fluxo_completo_do_modulo_financeiro()
    {

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = Url }).Build();
        var services = new ServiceCollection().AddLogging().AddInovabensCore(InovabensOpcoes.De(cfg, producao: false));
        await using var sp = services.BuildServiceProvider();
        using var escopo = sp.CreateScope();
        var r = escopo.ServiceProvider;
        r.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var auth = r.GetRequiredService<AuthService>();
        var imoveis = r.GetRequiredService<ImoveisService>();
        var aquisicao = r.GetRequiredService<AquisicaoService>();
        var lancamentos = r.GetRequiredService<LancamentosFinanceirosService>();
        var fechamentos = r.GetRequiredService<FechamentoService>();
        var indices = r.GetRequiredService<IndicesService>();
        var retorno = r.GetRequiredService<RetornoService>();
        var fabrica = r.GetRequiredService<IDbContextFactory<InovabensDb>>();

        var admin = (await auth.LoginAsync("admin@inovabens.com.br", "Trocar@123456", null)).Usuario!;
        var corretor = (await auth.LoginAsync("carla@inovabens.com.br", "Trocar@123456", null)).Usuario!;
        var id = await imoveis.CriarAsync(admin, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa para flip", Tipo = "casa" });
        var idCorretor = await imoveis.CriarAsync(corretor, new ImovelForm { NomeDrive = Guid.NewGuid().ToString(), Titulo = "Casa da Carla", Tipo = "casa" });

        // --- aquisição (aba Investimento, só administrador): total é coluna gerada; itens antes da aquisição são recusados ---
        Assert.True(admin.Pode(Permissoes.FinanceiroInvestimento));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => aquisicao.ObterAsync(corretor, idCorretor));
        Assert.Null(await aquisicao.ObterAsync(admin, id));
        await Assert.ThrowsAsync<RegraNegocioException>(() =>
            aquisicao.CriarItemAsync(admin, id, new AquisicaoItemForm { Descricao = "ITBI", Valor = 3000m }));
        await aquisicao.SalvarAsync(admin, id, new AquisicaoForm { DataAquisicao = D(2025, 1, 10), ValorPago = 100000m, Impostos = 1m });
        Assert.Equal(100001m, (await aquisicao.ObterAsync(admin, id))!.Dados.TotalAquisicao);
        await aquisicao.CriarItemAsync(admin, id, new AquisicaoItemForm { Tipo = TipoItemAquisicao.IMPOSTO, Descricao = "ITBI", Valor = 3000m });
        var aq = (await aquisicao.ObterAsync(admin, id))!;
        Assert.Equal(3000m, aq.Dados.Impostos);
        Assert.Equal(103000m, aq.Dados.TotalAquisicao);
        await aquisicao.ExcluirItemAsync(admin, aq.Itens[0].Id);
        aq = (await aquisicao.ObterAsync(admin, id))!;
        Assert.Empty(aq.Itens);
        Assert.Equal(100000m, aq.Dados.TotalAquisicao);

        // --- lançamentos financeiros: reforma paga (liquidada) e IPTU em aberto ---
        var contas = await lancamentos.ContasAsync(TipoConta.DESPESA);
        var manutencao = contas.Single(c => c.Nome == "Manutenção").Id;
        var reforma = await lancamentos.CriarAsync(admin, id, new LancamentoForm
        {
            Tipo = TipoConta.DESPESA, Data = D(2025, 4, 10), Valor = 10000m, Descricao = "Reforma", ContaId = manutencao, Liquidado = true, DataLiquidacao = D(2025, 4, 15),
        });
        await lancamentos.CriarAsync(admin, id, new LancamentoForm
        { Tipo = TipoConta.DESPESA, Data = D(2025, 5, 10), Valor = 500m, Descricao = "IPTU em aberto", ContaId = contas.Single(c => c.Nome == "IPTU").Id });
        var opex = await lancamentos.ResumoPorContaAsync(admin, id);
        Assert.Equal(-10000m, Assert.Single(opex).Total); // só liquidados

        // --- fechamento: venda líquida e interruptor dos lançamentos; marca o imóvel como Vendido ---
        Assert.NotEqual(SituacaoImovel.VENDIDO, (await imoveis.DetalheAsync(admin, id)).Dados.Situacao);
        await Assert.ThrowsAsync<AcessoNegadoException>(() => fechamentos.SalvarAsync(corretor, idCorretor, new FechamentoForm { Data = D(2025, 7, 20), ValorVenda = 1m }));
        await fechamentos.SalvarAsync(admin, id, new FechamentoForm { Data = D(2025, 7, 20), ValorVenda = 150000m, Comissao = 9000m, Impostos = 1000m });
        var fe = (await fechamentos.ObterAsync(admin, id))!;
        Assert.Equal(140000m, fe.ValorLiquido);
        Assert.Null(fe.IncluirLancamentos);
        Assert.Equal(SituacaoImovel.VENDIDO, (await imoveis.DetalheAsync(admin, id)).Dados.Situacao);

        // --- índices (manual: sem depender do BCB) e retorno nos dois cenários ---
        await Assert.ThrowsAsync<AcessoNegadoException>(() => indices.LancarManualAsync(corretor, CodigoIndice.IPCA, D(2025, 1, 1), 1m));
        for (var m = 1; m <= 7; m++) await indices.LancarManualAsync(admin, CodigoIndice.IPCA, D(2025, m, 1), 1m);

        var cen = await retorno.CenariosAsync(admin, id, CodigoIndice.IPCA, D(2025, 12, 31));
        Assert.Null(cen.Decisao);
        var ret = cen.Oficial; // sem decisão: cenário com os lançamentos
        Assert.Equal(100000.00m, ret.CustoAquisicao);
        Assert.Equal(110000.00m, ret.CustoTotal);
        Assert.Equal(500.00m, ret.PassivoPendente);
        Assert.Equal(140000.00m, ret.ReceitaLiquida);
        Assert.Equal(30000.00m, ret.Resultado);
        Assert.Equal(191, ret.PrazoDias);
        Assert.Equal(116455.03m, ret.CustoCorrigido);
        Assert.Equal(106152.02m, ret.Fluxo[0].ValorCorrigido);
        Assert.Equal(3, ret.Fluxo.Count);
        Assert.Empty(ret.Avisos);
        Assert.Equal(40000.00m, cen.SemLancamentos.Resultado);

        // o interruptor do fechamento escolhe o cenário oficial
        var form = FechamentoForm.De(fe);
        form.IncluirLancamentos = false;
        await fechamentos.SalvarAsync(admin, id, form);
        Assert.Equal(40000.00m, (await retorno.CalcularAsync(admin, id, CodigoIndice.IPCA, D(2025, 12, 31))).Resultado);

        // --- escopo: corretor não vê o imóvel de outro nem a aba Investimento ---
        await Assert.ThrowsAnyAsync<Exception>(() => retorno.CalcularAsync(corretor, id));
        await Assert.ThrowsAnyAsync<Exception>(() => lancamentos.ExtratoAsync(corretor, id, new FiltroLancamentos()));

        // --- telas: as abas renderizam com os dados gravados ---
        var htmlInv = await Renderizar<InvestimentoAba>(r, admin, id);
        Assert.DoesNotContain("msg-erro", htmlInv);
        foreach (var trecho in new[] { "Aquisição (CAPEX)", "Lançamentos Financeiros (OPEX)", "Fechamento da operação", "Retorno do investimento",
                     "100.000,00", "40.000,00", "30.000,00", "140.000,00", "Manuten", "ndices (BCB)" })
            Assert.Contains(trecho, htmlInv);
        var htmlLanc = await Renderizar<LancamentosFinanceirosAba>(r, admin, id);
        Assert.DoesNotContain("msg-erro", htmlLanc);
        foreach (var trecho in new[] { "Lançamentos Financeiros", "Nova receita", "Nova despesa", "Saldo real", "Em aberto", "Previstos" })
            Assert.Contains(trecho, htmlLanc);
        Assert.DoesNotContain("livro caixa", htmlLanc, StringComparison.OrdinalIgnoreCase);

        // --- auditoria gravada na mesma transação ---
        await using var db = await fabrica.CreateDbContextAsync();
        Assert.True(await db.AuditLog.AnyAsync(a => a.Entidade == "lancamentos_financeiros" && a.EntidadeId == reforma));
        Assert.True(await db.AuditLog.AnyAsync(a => a.Entidade == "fechamentos_investimento" && a.EntidadeId == id));
    }
    /// <summary>Consulta o BCB de verdade: só roda com INOVABENS_TEST_BCB=1.</summary>
    [Fact]
    public async Task Sincroniza_indices_com_o_Banco_Central()
    {
        if (Environment.GetEnvironmentVariable("INOVABENS_TEST_BCB") != "1") return;

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = Url }).Build();
        await using var sp = new ServiceCollection().AddLogging().AddInovabensCore(InovabensOpcoes.De(cfg, producao: false)).BuildServiceProvider();
        using var escopo = sp.CreateScope();
        var indices = escopo.ServiceProvider.GetRequiredService<IndicesService>();

        var res = await indices.SincronizarAsync();
        Assert.Equal(4, res.Count);
        // incremental: séries vazias vêm desde 2000; as já preenchidas, a partir do último mês gravado
        var incc = await indices.SerieAsync(CodigoIndice.INCC);
        Assert.True(incc.Count >= 300);
        Assert.Equal(D(2000, 1, 1), incc[0].Competencia);
        Assert.DoesNotContain(await indices.SincronizarAsync([CodigoIndice.INCC]), x => x.Value > 2);
    }

    /// <summary>Renderiza a aba no servidor (sem navegador) com o usuário em cascata, como faz o MainLayout.</summary>
    private static async Task<string> Renderizar<TAba>(IServiceProvider sp, UsuarioAtual usuario, Guid imovelId) where TAba : IComponent
    {
        var servicos = new ServiceCollection();
        foreach (var t in new[] { typeof(AquisicaoService), typeof(LancamentosFinanceirosService), typeof(FechamentoService), typeof(IndicesService),
                     typeof(RetornoService), typeof(ComprovantesService), typeof(BpoService), typeof(OpcoesService), typeof(FinanceiroEmpresaService) })
            servicos.AddSingleton(t, sp.GetRequiredService(t));
        servicos.AddSingleton(sp.GetRequiredService<ILoggerFactory>());
        servicos.AddSingleton<IJSRuntime, SemJs>();
        await using var provedor = servicos.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provedor, provedor.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var raiz = await renderer.RenderComponentAsync<CascadingValue<UsuarioAtual>>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = usuario,
                ["IsFixed"] = true,
                ["ChildContent"] = (RenderFragment)(b =>
                {
                    b.OpenComponent<TAba>(0);
                    b.AddAttribute(1, "ImovelId", imovelId);
                    b.CloseComponent();
                }),
            }));
            await raiz.QuiescenceTask;
            return raiz.ToHtmlString();
        });
    }

    private sealed class SemJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new InvalidOperationException("JS indisponível no teste");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken ct, object?[]? args) => throw new InvalidOperationException("JS indisponível no teste");
    }
}
