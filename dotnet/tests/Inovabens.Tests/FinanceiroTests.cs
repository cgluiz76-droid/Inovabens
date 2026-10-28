using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Dominio;
using Inovabens.Core.Financeiro;

namespace Inovabens.Tests;

/// <summary>Mesmo cenário conferido à mão no teste e2e do Node (imoveis-financeiro.e2e.spec.ts).</summary>
public class FinanceiroTests
{
    private static DateOnly D(int a, int m, int d) => new(a, m, d);

    private static DadosRetorno Cenario(FatoresIndice fatores) => new(
        Guid.NewGuid(), CodigoIndice.IPCA, D(2025, 12, 31),
        new Aquisicao { DataAquisicao = D(2025, 1, 10), ValorPago = 100000m, TotalAquisicao = 100000m },
        [],
        [
            new(D(2025, 4, 1), D(2025, 4, 10), D(2025, 4, 15), StatusLancamento.PAGO, 10000m, "Reforma", "Manutenção"),
            new(D(2025, 5, 1), D(2025, 5, 10), null, StatusLancamento.NAO_PAGO, 500m, "IPTU em aberto", "Impostos"),
        ],
        [new Receita { Tipo = TipoReceita.VENDA, Data = D(2025, 7, 20), ValorBruto = 150000m, CustosVenda = 9000m, Tributos = 1000m, ValorLiquido = 140000m }],
        fatores);

    private static FatoresIndice Ipca1PorCento() =>
        new(Enumerable.Range(1, 7).Select(m => (D(2025, m, 1), 1m)));

    [Fact]
    public void Retorno_bate_com_o_calculo_do_Node()
    {
        var r = CalculoRetorno.Calcular(Cenario(Ipca1PorCento()));

        Assert.Equal(100000.00m, r.CustoAquisicao);
        Assert.Equal(110000.00m, r.CustoTotal);
        Assert.Equal(500.00m, r.PassivoPendente);
        Assert.Equal(140000.00m, r.ReceitaLiquida);
        Assert.Equal(30000.00m, r.Resultado);
        Assert.Equal(30000.0 / 110000, r.Roi!.Value, 5);
        Assert.Equal(140000.0 / 110000, r.Moic!.Value, 5);
        Assert.Equal(D(2025, 7, 20), r.DataFinal);
        Assert.Equal(191, r.PrazoDias);
        Assert.Equal(116455.03m, r.CustoCorrigido);
        Assert.Equal(3, r.Fluxo.Count);
        Assert.Equal(106152.02m, r.Fluxo[0].ValorCorrigido);
        Assert.Empty(r.Avisos);
        Assert.True(r.TirAnual > 0.5);

        // VPL na TIR encontrada ≈ 0
        var tir = r.TirAnual!.Value;
        var t0 = r.Fluxo[0].Data;
        var vpl = r.Fluxo.Sum(f =>
            (f.Tipo == TipoMovimento.SAIDA ? -(double)f.Valor : (double)f.Valor) /
            Math.Pow(1 + tir, (f.Data.DayNumber - t0.DayNumber) / 365.0));
        Assert.True(Math.Abs(vpl) < 0.5, $"VPL = {vpl}");
    }

    [Fact]
    public void Serie_vazia_gera_aviso_e_nao_corrige()
    {
        var r = CalculoRetorno.Calcular(Cenario(new FatoresIndice([])));
        Assert.Contains(r.Avisos, a => a.Contains("vazia"));
        Assert.Equal(30000.00m, r.Resultado);
    }

    [Fact]
    public void Sem_receita_indicadores_ficam_nulos()
    {
        var d = Cenario(Ipca1PorCento()) with { Receitas = [] };
        var r = CalculoRetorno.Calcular(d);
        Assert.Null(r.Roi);
        Assert.Null(r.Moic);
        Assert.Null(r.TirAnual);
    }

    [Fact]
    public void Fatores_acumulam_e_seguram_o_ultimo_mes()
    {
        var f = Ipca1PorCento();
        Assert.Equal(1.01, f.Fator(D(2025, 1, 31)), 12);
        Assert.Equal(Math.Pow(1.01, 3), f.Fator(D(2025, 3, 5)), 12);
        Assert.Equal(Math.Pow(1.01, 7), f.Fator(D(2026, 6, 1)), 12);
        Assert.Equal(1.0, f.Fator(D(2024, 12, 1)), 12);
        Assert.Equal(D(2025, 1, 1), f.PrimeiroMes);
        Assert.Equal(D(2025, 7, 1), f.UltimoMes);
    }

    [Fact]
    public void Xirr_de_fluxo_simples_de_um_ano()
    {
        var tir = CalculoRetorno.Xirr([(D(2025, 1, 1), -1000), (D(2026, 1, 1), 1100)]);
        Assert.NotNull(tir);
        Assert.Equal(0.10, tir!.Value, 4);
    }

    [Fact]
    public void Xirr_sem_troca_de_sinal_e_nulo() =>
        Assert.Null(CalculoRetorno.Xirr([(D(2025, 1, 1), -1000), (D(2026, 1, 1), -100)]));

    private static List<ValidationResult> Validar(object o)
    {
        var res = new List<ValidationResult>();
        Validator.TryValidateObject(o, new ValidationContext(o), res, true);
        return res;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("curta", false)]
    [InlineData("   nove ch   ", false)]
    [InlineData("Inquilino atrasou o repasse", true)]
    public void Nao_pago_exige_justificativa(string? just, bool valido)
    {
        var e = new StatusLancamentoEntrada { Status = StatusLancamento.NAO_PAGO, Justificativa = just };
        Assert.Equal(valido, Validar(e).Count == 0);
    }

    [Fact]
    public void Pago_nao_exige_justificativa_mas_valida_valor()
    {
        Assert.Empty(Validar(new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, Valor = 10.5m }));
        Assert.NotEmpty(Validar(new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, Valor = -1m }));
        Assert.NotEmpty(Validar(new StatusLancamentoEntrada { Status = StatusLancamento.PAGO, Valor = 1.234m }));
    }
}
