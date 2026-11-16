using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
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
            new(D(2025, 4, 15), -10000m, SituacaoLancamento.LIQUIDADO, "Reforma", "Manutenção"),
            new(D(2025, 5, 10), -500m, SituacaoLancamento.ABERTO, "IPTU em aberto", "IPTU"),
        ],
        new FechamentoInvestimento { Data = D(2025, 7, 20), ValorVenda = 150000m, Comissao = 9000m, Impostos = 1000m },
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
    public void Sem_fechamento_indicadores_ficam_nulos()
    {
        var d = Cenario(Ipca1PorCento()) with { Fechamento = null, Lancamentos = [] };
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

    [Fact]
    public void Sem_os_lancamentos_o_custo_e_so_a_aquisicao()
    {
        var r = CalculoRetorno.Calcular(Cenario(Ipca1PorCento()) with { IncluirLancamentos = false });
        Assert.Equal(100000.00m, r.CustoTotal);
        Assert.Equal(0m, r.PassivoPendente);
        Assert.Equal(40000.00m, r.Resultado);
        Assert.Equal(-10000.00m, r.SaldoLancamentos);
        Assert.False(r.IncluiLancamentos);
        Assert.Equal(140000.00m, r.VendaLiquida);
    }

    [Fact]
    public void Receita_liquidada_entra_como_entrada_e_previsto_nao_entra()
    {
        var d = Cenario(Ipca1PorCento());
        d = d with { Lancamentos = [.. d.Lancamentos, new(D(2025, 6, 1), 2000m, SituacaoLancamento.LIQUIDADO, "Reembolso", "Reembolso"),
            new(D(2025, 6, 5), -300m, SituacaoLancamento.PREVISTO, "Condomínio", "Condomínio")] };
        var r = CalculoRetorno.Calcular(d);
        Assert.Equal(142000.00m, r.ReceitaLiquida);
        Assert.Equal(800.00m, r.PassivoPendente); // IPTU aberto + condomínio previsto, ambos vencidos
        Assert.Equal(-8000.00m, r.SaldoLancamentos);
    }

    [Fact]
    public void Despesa_entra_com_sinal_negativo_e_receita_positivo()
    {
        var conta = Guid.NewGuid();
        var l = new LancamentoFinanceiro();
        new LancamentoForm { Tipo = TipoConta.DESPESA, Data = D(2026, 11, 10), Valor = 350m, Descricao = "Condomínio", ContaId = conta }.AplicarEm(l);
        Assert.Equal(-350m, l.Valor);
        Assert.Equal(SituacaoLancamento.ABERTO, l.Situacao);
        Assert.Null(l.DataLiquidacao);

        new LancamentoForm
        {
            Tipo = TipoConta.RECEITA, Data = D(2026, 11, 5), Valor = 1200m, Descricao = "Aluguel", ContaId = conta, Liquidado = true, DataLiquidacao = D(2026, 11, 6),
            Recorrente = true, DiaRecorrencia = 5, ValorEstimado = 1200m,
        }.AplicarEm(l);
        Assert.Equal(1200m, l.Valor);
        Assert.Equal(1200m, l.ValorEstimado);
        Assert.Equal(SituacaoLancamento.LIQUIDADO, l.Situacao);
        Assert.Equal(D(2026, 11, 6), l.DataLiquidacao);
    }

    [Fact]
    public void Formulario_do_lancamento_valida_valor_liquidacao_e_recorrencia()
    {
        LancamentoForm F() => new() { Tipo = TipoConta.DESPESA, Data = D(2026, 11, 10), Valor = 10m, Descricao = "Luz", ContaId = Guid.NewGuid() };
        Assert.Empty(Validar(F()));
        var f = F(); f.Valor = 0; Assert.NotEmpty(Validar(f));
        f = F(); f.Valor = -5; Assert.NotEmpty(Validar(f));
        f = F(); f.Valor = 1.234m; Assert.NotEmpty(Validar(f));
        f = F(); f.Liquidado = true; Assert.Contains(Validar(f), r => r.MemberNames.Contains(nameof(LancamentoForm.DataLiquidacao)));
        f = F(); f.Recorrente = true; f.DiaRecorrencia = 32; f.ValorEstimado = 10m; Assert.Contains(Validar(f), r => r.MemberNames.Contains(nameof(LancamentoForm.DiaRecorrencia)));
        f = F(); f.Recorrente = true; f.DiaRecorrencia = 10; Assert.Contains(Validar(f), r => r.MemberNames.Contains(nameof(LancamentoForm.ValorEstimado)));
        f = F(); f.Recorrente = true; f.DiaRecorrencia = 10; f.ValorEstimado = 10m; Assert.Empty(Validar(f));
    }

    [Fact]
    public void Numero_do_titulo_usa_o_codigo_do_imovel()
    {
        Assert.Equal("CA0013-0007", LancamentosFinanceirosService.FormatarNumero("CA0013", 7));
        Assert.Equal("—", LancamentosFinanceirosService.FormatarNumero("CA0013", null));
    }

    [Fact]
    public void Fechamento_exige_venda_positiva()
    {
        Assert.NotEmpty(Validar(new FechamentoForm { Data = D(2026, 1, 1), ValorVenda = 0 }));
        Assert.NotEmpty(Validar(new FechamentoForm { Data = D(2026, 1, 1), ValorVenda = 100, Comissao = -1 }));
        var ok = new FechamentoForm { Data = D(2026, 1, 1), ValorVenda = 100, Comissao = 6, Impostos = 4 };
        Assert.Empty(Validar(ok));
        Assert.Equal(90m, ok.Liquido);
    }

    [Fact]
    public void Resposta_do_Banco_Central_vira_competencia_mensal()
    {
        var pontos = IndicesService.LerPontos([new("01/01/2025", "0.16"), new("01/12/2025", "-0.05")]);
        Assert.Equal([(D(2025, 1, 1), 0.16m), (D(2025, 12, 1), -0.05m)], pontos);
    }

    [Theory]
    [InlineData("2025-01-01", "0.16")]
    [InlineData("01/13/2025", "0.16")]
    [InlineData("01/01/2025", "0,16")]
    [InlineData(null, "0.16")]
    public void Resposta_inesperada_do_Banco_Central_e_erro_controlado(string? data, string valor) =>
        Assert.Throws<ServicoIndisponivelException>(() => IndicesService.LerPontos([new(data!, valor)]));
}
