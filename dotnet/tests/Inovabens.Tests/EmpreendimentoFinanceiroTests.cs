using Inovabens.Core;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Inovabens.Tests;

/// <summary>Rateio hierárquico por área: custo da matrícula vai para as unidades ativas abaixo dela, congelado na venda.</summary>
public class RateioEmpreendimentoTests
{
    // TE (1.000 m²) → L1, L2, L3 (200 m² cada) + R1 (350 m²) [50 m² de rua]; R1 → L4, L5 (150 m²) + R2 (50 m²)
    private static readonly Guid Te = Guid.NewGuid(), L1 = Guid.NewGuid(), L2 = Guid.NewGuid(), L3 = Guid.NewGuid(), R1 = Guid.NewGuid(),
        L4 = Guid.NewGuid(), L5 = Guid.NewGuid(), R2 = Guid.NewGuid();

    private static List<NoRateio> Arvore(DateOnly? vendaL1 = null) =>
    [
        new(Te, null, "TE", 1000, null),
        new(L1, Te, "TE-01", 200, vendaL1), new(L2, Te, "TE-02", 200, null), new(L3, Te, "TE-03", 200, null), new(R1, Te, "TE-R1", 350, null),
        new(L4, R1, "TE-04", 150, null), new(L5, R1, "TE-05", 150, null), new(R2, R1, "TE-R2", 50, null),
    ];

    private static CustoNo Custo(Guid no, decimal valor, DateOnly data, string desc = "Custo") =>
        new(no, new MovimentoAdicional(data, TipoMovimento.SAIDA, desc, valor, OrigemMovimento.Aquisicao));

    private static decimal Total(ResultadoRateio r, Guid id) => r.De(id).Sum(m => m.Valor);

    [Fact]
    public void Custo_da_raiz_desce_pela_area_e_a_remanescente_repassa_aos_filhos()
    {
        var r = RateioEmpreendimento.Calcular(Arvore(), [Custo(Te, 95_000, new(2024, 1, 1))]);
        // 950 m² vendáveis: 200/950 = 20.000; a R1 recebe 35.000 e repassa 15.000 + 15.000 + 5.000
        Assert.Equal(20_000, Total(r, L1));
        Assert.Equal(35_000, Total(r, R1));
        Assert.Equal((15_000m, 15_000m, 5_000m), (Total(r, L4), Total(r, L5), Total(r, R2)));
        Assert.Equal(95_000, new[] { L1, L2, L3, L4, L5, R2 }.Sum(x => Total(r, x)));
        Assert.Empty(r.NaoRateados);
        Assert.StartsWith("Rateio de TE (", r.De(L4)[0].Descricao);
    }

    [Fact]
    public void Lote_vendido_fica_congelado_e_custo_da_remanescente_nao_vai_para_a_fase_1()
    {
        var r = RateioEmpreendimento.Calcular(Arvore(vendaL1: new(2024, 6, 30)),
        [
            Custo(Te, 7_500, new(2024, 7, 1)),   // depois da venda do L1: só L2, L3 e R1 (750 m²)
            Custo(R1, 3_500, new(2024, 8, 1)),   // taxa do 2º desmembramento: só os filhos da R1
            Custo(Te, 9_500, new(2024, 6, 30)),  // no dia da venda o L1 ainda participa
        ]);
        // 9.500 em 30/06 (950 m²): L1 2.000 · L2 2.000 · L3 2.000 · R1 3.500 (L4 1.500, L5 1.500, R2 500)
        // 7.500 em 01/07 (750 m², sem o L1): L2 2.000 · L3 2.000 · R1 3.500 (L4 1.500, L5 1.500, R2 500)
        // 3.500 na R1 em 01/08: L4 1.500, L5 1.500, R2 500
        Assert.Equal(2_000, Total(r, L1));
        Assert.Equal((4_000m, 4_000m), (Total(r, L2), Total(r, L3)));
        Assert.Equal((4_500m, 4_500m, 1_500m), (Total(r, L4), Total(r, L5), Total(r, R2)));
        Assert.Equal(7_000, Total(r, R1)); // o que chegou de cima; o custo próprio da R1 não conta como recebido
        Assert.Equal(20_500, new[] { L1, L2, L3, L4, L5, R2 }.Sum(x => Total(r, x)));
    }

    [Fact]
    public void Sem_unidade_ativa_o_custo_fica_no_empreendimento()
    {
        var nos = new List<NoRateio> { new(Te, null, "TE", 100, null), new(L1, Te, "TE-01", 100, new(2024, 1, 1)) };
        var r = RateioEmpreendimento.Calcular(nos, [Custo(Te, 1_000, new(2024, 2, 1))]);
        Assert.Single(r.NaoRateados);
        Assert.Equal(0, Total(r, L1));
    }

    [Fact]
    public void Permuta_com_imovel_maior_gera_torna_paga()
    {
        var r = new FechamentoRecebimento { Tipo = TipoRecebimento.PERMUTA_IMOVEL, Valor = 500_000, ValorBem = 700_000 };
        Assert.Equal(200_000, r.TornaPaga);
        r.ValorBem = 500_000;
        Assert.Equal(0, r.TornaPaga);
    }

    [Fact]
    public void Sem_area_divide_em_partes_iguais_e_avisa()
    {
        var nos = new List<NoRateio> { new(Te, null, "TE", null, null), new(L1, Te, "TE-01", null, null), new(L2, Te, "TE-02", 300, null), new(L3, Te, "TE-03", 300, null) };
        var r = RateioEmpreendimento.Calcular(nos, [Custo(Te, 100, new(2024, 1, 1))]);
        Assert.Equal((33.33m, 33.33m, 33.34m), (Total(r, L1), Total(r, L2), Total(r, L3)));
        Assert.Single(r.Avisos);
    }
}

/// <summary>Empreendimento no financeiro: retorno do lote com rateio, consolidado, painel e venda com permuta e torna.</summary>
[Collection(BancoDeTeste.Nome)]
[Trait("Categoria", "Integracao")]
public class EmpreendimentoFinanceiroTests : IAsyncLifetime
{
    private IServiceScope _escopo = default!;
    private T S<T>() where T : notnull => _escopo.ServiceProvider.GetRequiredService<T>();

    public Task InitializeAsync()
    {
        _escopo = ExtratoComprovantesTests.Compartilhado.CreateScope();
        _escopo.ServiceProvider.GetRequiredService<ContextoConexao>().Ip = "127.0.0.1";
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _escopo.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Lote_vendido_com_permuta_tem_custo_rateado_e_o_imovel_recebido_vira_aquisicao()
    {
        var admin = (await S<AuthService>().LoginAsync("admin@inovabens.com.br", BancoFixture.Senha, null)).Usuario!;
        var imoveis = S<ImoveisService>();
        var emp = S<EmpreendimentosService>();
        var retorno = S<RetornoService>();
        var fech = S<FechamentoService>();
        var marca = Guid.NewGuid().ToString("N")[..8];
        var raizId = await imoveis.CriarAsync(admin, new ImovelForm
        {
            NomeDrive = "Gleba " + marca, Titulo = "Gleba " + marca, Tipo = "casa", Cidade = "Inocência", Uf = "MS", Matricula = "9.000", AreaTerrenoM2 = 1000,
        });
        await S<AquisicaoService>().SalvarAsync(admin, raizId, new AquisicaoForm { DataAquisicao = new(2024, 1, 10), ValorPago = 95_000 });

        // 2 lotes de 200 m² + remanescente de 550 m² + 50 m² de rua: o lote leva 200/950 do custo = 20.000
        await emp.DesmembrarAsync(admin, raizId, new DesmembramentoForm
        {
            Quantidade = 2, AreaLoteM2 = 200, PrecoLote = 100_000, Remanescente = true, AreaRemanescenteM2 = 550, Fase = new FaseForm { AreaDoadaM2 = 50 },
        });
        var d = (await emp.DetalheAsync(admin, raizId))!;
        var lote = UnidadeEdicao.De(d.Unidades[0]);
        lote.Matricula = "9.001";
        await emp.SalvarUnidadesAsync(admin, raizId, [lote]);

        // o empreendimento desmembrado não tem venda própria
        await Assert.ThrowsAsync<RegraNegocioException>(() => fech.SalvarAsync(admin, raizId, new FechamentoForm { Data = new(2024, 6, 1), ValorVenda = 1 }));

        // venda de 100.000: casa que vale 80.000 entra por 60.000 (torna paga 20.000) + 40.000 em dinheiro
        var venda = new FechamentoForm
        {
            Data = new(2024, 6, 1), ValorVenda = 100_000, Comissao = 5_000, IncluirLancamentos = true,
            Recebimentos = [new() { Tipo = TipoRecebimento.PERMUTA_IMOVEL, Valor = 60_000, ValorBem = 80_000, Descricao = "Casa na Rua X" }],
        };
        await Assert.ThrowsAnyAsync<Exception>(() => fech.SalvarAsync(admin, lote.Id, venda)); // soma 60.000 ≠ 100.000
        venda.Recebimentos.Add(new() { Tipo = TipoRecebimento.DINHEIRO, Valor = 40_000 });
        await fech.SalvarAsync(admin, lote.Id, venda);

        var rLote = await retorno.CalcularAsync(admin, lote.Id);
        Assert.Equal((20_000m, 95_000m, 75_000m), (rLote.CustoTotal, rLote.ReceitaLiquida, rLote.Resultado));
        Assert.Contains(rLote.Fluxo, m => m.Descricao.StartsWith($"Rateio de {d.Codigo}"));

        // consolidado: todo o custo do empreendimento e a venda do lote
        var rRaiz = await retorno.CalcularAsync(admin, raizId);
        Assert.Equal((95_000m, 95_000m), (rRaiz.CustoTotal, rRaiz.ReceitaLiquida));

        var painel = (await retorno.PainelAsync(admin, raizId))!;
        var t = painel.Total;
        Assert.Equal((3, 1, 95_000m, 20_000m, 75_000m), (t.Unidades.Count, t.Vendidas, t.CustoTotal, t.CustoVendidas, t.Resultado));
        Assert.Equal((40_000m, 60_000m, 20_000m, 0m), (t.EmDinheiro, t.EmBens, t.TornaPaga, t.AReceber));
        Assert.Equal(100_000 + 100_000 + 0, t.Vgv); // lote vendido + lote a 100.000 + remanescente sem preço

        // o imóvel recebido vira um imóvel novo com aquisição de 80.000 (permuta + torna)
        var form = (await fech.FormAsync(admin, lote.Id))!;
        var permuta = form.Recebimentos.Single(r => r.Tipo == TipoRecebimento.PERMUTA_IMOVEL);
        var casaId = await fech.RegistrarImovelRecebidoAsync(admin, lote.Id, permuta.Id!.Value, new ImovelRecebidoForm { Nome = "Casa Rua X " + marca, Tipo = "casa" });
        var aq = (await S<AquisicaoService>().ObterAsync(admin, casaId))!;
        Assert.Equal((80_000m, new DateOnly(2024, 6, 1)), (aq.Dados.ValorPago, aq.Dados.DataAquisicao));
        Assert.Contains("torna", aq.Dados.FormaPagamento);
        Assert.NotNull((await fech.FormAsync(admin, lote.Id))!.Recebimentos.Single(r => r.Id == permuta.Id).ImovelRecebidoCodigo);
        await Assert.ThrowsAsync<RegraNegocioException>(() => fech.RegistrarImovelRecebidoAsync(admin, lote.Id, permuta.Id!.Value, new ImovelRecebidoForm { Nome = "De novo" }));
        await Assert.ThrowsAsync<RegraNegocioException>(() => fech.ExcluirAsync(admin, lote.Id));
    }
}
