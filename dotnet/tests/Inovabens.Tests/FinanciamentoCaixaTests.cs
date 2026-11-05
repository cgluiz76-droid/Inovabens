using Inovabens.Core.Imoveis;
using Xunit;

namespace Inovabens.Tests;

/// <summary>Simulador do site com os parâmetros da Caixa (SBPE): cota, prazo, idade, juros nominais / 12.</summary>
public class FinanciamentoCaixaTests
{
    [Fact]
    public void Sac_financia_80_por_cento_e_calcula_juros_com_taxa_nominal_dividida_por_12()
    {
        var r = FinanciamentoCaixa.Simular(500_000m, 0, 420, 0.1149m, sac: true);

        Assert.Equal(100_000m, r.Entrada);           // entrada mínima de 20%
        Assert.Equal(400_000m, r.ValorFinanciado);
        Assert.Equal(4_782.38m, r.PrimeiraParcela);   // 400.000/420 + 400.000 × 11,49%/12
        Assert.Equal(961.50m, r.UltimaParcela);       // 952,38 + 952,38 × 0,9575%
        Assert.Equal(15_941.27m, r.RendaMinima);      // parcela até 30% da renda
        Assert.True(r.Sfh);
        Assert.Equal(0.1211m, r.TaxaEfetivaAnual);
    }

    [Fact]
    public void Price_financia_no_maximo_70_por_cento_com_parcela_fixa()
    {
        var r = FinanciamentoCaixa.Simular(500_000m, 50_000m, 420, 0.1149m, sac: false);

        Assert.Equal(150_000m, r.Entrada);           // entrada sobe para 30%
        Assert.Equal(350_000m, r.ValorFinanciado);
        Assert.Equal(r.PrimeiraParcela, r.UltimaParcela);
        Assert.Equal(3_413.62m, r.PrimeiraParcela); // PMT(0,9575% a.m.; 420; 350.000)
    }

    [Theory]
    [InlineData(null, 420)]
    [InlineData(30, 420)]
    [InlineData(46, 414)]  // 80a6m − 46a = 34a6m
    [InlineData(60, 246)]
    [InlineData(81, 0)]
    public void Idade_mais_prazo_nao_passa_de_80_anos_e_6_meses(int? idade, int prazoMaximo) =>
        Assert.Equal(prazoMaximo, FinanciamentoCaixa.PrazoMaximoPara(idade));

    [Fact]
    public void Prazo_e_limitado_pela_idade_na_simulacao()
    {
        var r = FinanciamentoCaixa.Simular(300_000m, 60_000m, 420, 0.1149m, sac: true, idadeAnos: 60);
        Assert.Equal(246, r.Prazo);
    }

    [Theory]
    [InlineData(2_250_000, true, 0.1149)]
    [InlineData(2_250_001, false, 0.12)]
    public void Acima_do_teto_do_sfh_usa_a_taxa_do_sfi(decimal valor, bool sfh, decimal taxa)
    {
        Assert.Equal(sfh, FinanciamentoCaixa.Sfh(valor));
        Assert.Equal(taxa, FinanciamentoCaixa.TaxaPadrao(valor));
    }
}
