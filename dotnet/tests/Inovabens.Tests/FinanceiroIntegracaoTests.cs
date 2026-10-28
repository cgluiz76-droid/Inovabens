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
/// mais ao menos uma categoria em despesa_categorias e financeiro:ver/editar no perfil Corretor).
/// Reproduz o cenário do e2e do Node e confere os mesmos números.
/// </summary>
[Collection(BancoDeTeste.Nome)]
public class FinanceiroIntegracaoTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("INOVABENS_TEST_DB");
    private static DateOnly D(int a, int m, int d) => new(a, m, d);

    [Fact]
    public async Task Fluxo_completo_do_modulo_financeiro()
    {
        if (Url is null) return; // sem banco de teste configurado

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = Url }).Build();
        var services = new ServiceCollection().AddLogging().AddInovabensCore(InovabensOpcoes.De(cfg, producao: false));
        await using var sp = services.BuildServiceProvider();
        using var escopo = sp.CreateScope();
        var r = escopo.ServiceProvider;
        r.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        var auth = r.GetRequiredService<AuthService>();
        var imoveis = r.GetRequiredService<ImoveisService>();
        var aquisicao = r.GetRequiredService<AquisicaoService>();
        var despesas = r.GetRequiredService<DespesasService>();
        var receitas = r.GetRequiredService<ReceitasService>();
        var indices = r.GetRequiredService<IndicesService>();
        var retorno = r.GetRequiredService<RetornoService>();
        var fabrica = r.GetRequiredService<IDbContextFactory<InovabensDb>>();

        var admin = (await auth.LoginAsync("admin@inovabens.com.br", "Trocar@123456", null)).Usuario!;
        var corretor = (await auth.LoginAsync("carla@inovabens.com.br", "Trocar@123456", null)).Usuario!;
        var id = await imoveis.CriarAsync(admin, new ImovelForm { Titulo = "Casa para flip", Tipo = "casa" });
        var idCorretor = await imoveis.CriarAsync(corretor, new ImovelForm { Titulo = "Casa da Carla", Tipo = "casa" });

        // --- aquisição: total é coluna gerada; itens antes da aquisição são recusados ---
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

        // --- despesas e checklist ---
        var cat = (await despesas.CategoriasAsync())[0];
        var condominio = await despesas.CriarAsync(admin, id, new DespesaForm
        { CategoriaId = cat.Id, Descricao = "Condomínio", ValorPrevisto = 800m, DiaVencimento = 31, InicioEm = D(2025, 1, 1), FimEm = D(2025, 3, 31) });
        await despesas.CriarAsync(admin, id, new DespesaForm
        { CategoriaId = cat.Id, Descricao = "Seguro", Recorrencia = Recorrencia.ANUAL, ValorPrevisto = 1200m, InicioEm = D(2025, 2, 5) });
        await despesas.CriarAsync(admin, id, new DespesaForm
        { CategoriaId = cat.Id, Descricao = "Reforma", Recorrencia = Recorrencia.UNICA, ValorPrevisto = 10000m, InicioEm = D(2025, 4, 1) });
        await despesas.CriarAsync(admin, id, new DespesaForm
        { CategoriaId = cat.Id, Descricao = "IPTU em aberto", Recorrencia = Recorrencia.UNICA, ValorPrevisto = 500m, InicioEm = D(2025, 5, 1) });

        var fev = await despesas.ChecklistAsync(admin, id, D(2025, 2, 17));
        Assert.Equal(D(2025, 2, 1), fev.Mes);
        Assert.Equal(2, fev.Resumo.AMarcar); // condomínio + seguro (ANUAL, mês de início)
        Assert.Equal(D(2025, 2, 28), fev.Itens.Single(i => i.Descricao == "Condomínio").Vencimento); // dia 31 → último dia
        Assert.Equal(0, await despesas.GerarCompetenciaAsync(D(2025, 2, 1), id)); // idempotente
        Assert.DoesNotContain((await despesas.ChecklistAsync(admin, id, D(2025, 3, 1))).Itens, i => i.Descricao == "Seguro");
        Assert.DoesNotContain((await despesas.ChecklistAsync(admin, id, D(2025, 4, 1))).Itens, i => i.Descricao == "Condomínio"); // após fim_em

        // fevereiro: condomínio N/A, seguro N/A (para não entrarem no cenário do retorno)
        foreach (var i in fev.Itens)
            await despesas.AtualizarStatusAsync(admin, i.LancamentoId, new StatusLancamentoEntrada { Status = StatusLancamento.NAO_SE_APLICA });
        foreach (var m in new[] { 1, 3 })
            foreach (var i in (await despesas.ChecklistAsync(admin, id, D(2025, m, 1))).Itens)
                await despesas.AtualizarStatusAsync(admin, i.LancamentoId, new StatusLancamentoEntrada { Status = StatusLancamento.NAO_SE_APLICA });

        var reforma = (await despesas.ChecklistAsync(admin, id, D(2025, 4, 1))).Itens.Single();
        await despesas.AtualizarStatusAsync(admin, reforma.LancamentoId, new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, DataPagamento = D(2025, 4, 15) });
        var iptu = (await despesas.ChecklistAsync(admin, id, D(2025, 5, 1))).Itens.Single();
        var erro = await Assert.ThrowsAsync<RegraNegocioException>(() =>
            despesas.AtualizarStatusAsync(admin, iptu.LancamentoId, new StatusLancamentoEntrada { Status = StatusLancamento.NAO_PAGO, Justificativa = "curta" }));
        Assert.Contains("Justificativa", erro.Message + string.Join(" ", erro.Erros.SelectMany(e => e.Value)));
        await despesas.AtualizarStatusAsync(admin, iptu.LancamentoId, new StatusLancamentoEntrada { Status = StatusLancamento.NAO_PAGO, Justificativa = "Aguardando segunda via" });
        var maio = await despesas.ChecklistAsync(admin, id, D(2025, 5, 1));
        Assert.Equal(1, maio.Resumo.NaoPago);
        Assert.Equal("Aguardando segunda via", maio.Itens[0].Justificativa);

        // exclusão lógica: some da lista, não gera mais competências
        await despesas.ExcluirAsync(admin, condominio);
        Assert.DoesNotContain(await despesas.ListarAsync(admin, id), d => d.Dados.Id == condominio);

        // --- receitas: líquido é coluna gerada ---
        var venda = await receitas.CriarAsync(admin, id, new ReceitaForm
        { Tipo = TipoReceita.VENDA, Data = D(2025, 7, 20), ValorBruto = 150000m, CustosVenda = 9000m, Tributos = 1000m });
        Assert.Equal(140000m, (await receitas.ListarAsync(admin, id)).Single(x => x.Id == venda.Id).ValorLiquido);
        var extra = await receitas.CriarAsync(admin, id, new ReceitaForm { Tipo = TipoReceita.OUTRA, Data = D(2025, 6, 1), ValorBruto = 1m });
        await receitas.ExcluirAsync(admin, extra.Id);
        Assert.Single(await receitas.ListarAsync(admin, id));

        // --- índices (manual: sem depender do BCB) e retorno ---
        await Assert.ThrowsAsync<AcessoNegadoException>(() => indices.LancarManualAsync(corretor, CodigoIndice.IPCA, D(2025, 1, 1), 1m));
        for (var m = 1; m <= 7; m++) await indices.LancarManualAsync(admin, CodigoIndice.IPCA, D(2025, m, 1), 1m);

        var ret = await retorno.CalcularAsync(admin, id, CodigoIndice.IPCA, D(2025, 12, 31));
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

        // --- escopo do corretor: imóvel de outro corretor é invisível ---
        await Assert.ThrowsAnyAsync<Exception>(() => retorno.CalcularAsync(corretor, id));
        await Assert.ThrowsAnyAsync<Exception>(() => despesas.ChecklistAsync(corretor, id, D(2025, 5, 1)));

        // --- tela: a aba Financeiro renderiza as quatro seções com os dados gravados ---
        var html = await RenderizarAba(r, admin, id);
        Assert.DoesNotContain("msg-erro", html);
        foreach (var trecho in new[] { "Aquisição (CAPEX)", "checklist mensal", "Receitas (venda / locação)", "Retorno do investimento",
                     "100.000,00", "110.000,00", "140.000,00", "30.000,00", "27,27%", "Reforma", "ndices (BCB)" })
            Assert.Contains(trecho, html);
        Assert.DoesNotContain("ndices (BCB)", await RenderizarAba(r, corretor, idCorretor));

        // --- auditoria gravada na mesma transação ---
        await using var db = await fabrica.CreateDbContextAsync();
        Assert.True(await db.AuditLog.AnyAsync(a => a.Entidade == "despesa_lancamentos" && a.EntidadeId == iptu.LancamentoId));
        Assert.True(await db.AuditLog.AnyAsync(a => a.Entidade == "receitas" && a.EntidadeId == venda.Id));
    }

    /// <summary>Consulta o BCB de verdade: só roda com INOVABENS_TEST_DB e INOVABENS_TEST_BCB=1.</summary>
    [Fact]
    public async Task Sincroniza_indices_com_o_Banco_Central()
    {
        if (Url is null || Environment.GetEnvironmentVariable("INOVABENS_TEST_BCB") != "1") return;

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
    private static async Task<string> RenderizarAba(IServiceProvider sp, UsuarioAtual usuario, Guid imovelId)
    {
        var servicos = new ServiceCollection();
        foreach (var t in new[] { typeof(AquisicaoService), typeof(DespesasService), typeof(ReceitasService), typeof(IndicesService), typeof(RetornoService) })
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
                    b.OpenComponent<FinanceiroAba>(0);
                    b.AddAttribute(1, nameof(FinanceiroAba.ImovelId), imovelId);
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
