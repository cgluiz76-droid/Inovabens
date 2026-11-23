using Inovabens.Core.Dominio;

namespace Inovabens.Core.Financeiro;

public enum TipoMovimento { SAIDA, ENTRADA }

public sealed record MovimentoFluxo(DateOnly Data, TipoMovimento Tipo, string Descricao, decimal Valor, decimal? ValorCorrigido);

/// <summary>Percentuais em fração (0,153 = 15,3%). Indicadores nulos = não calculáveis (ex.: sem receita).</summary>
public sealed record ResultadoRetorno(
    Guid ImovelId, DateOnly Ate, DateOnly DataFinal, CodigoIndice Indice,
    decimal CustoAquisicao, decimal CustoTotal, decimal PassivoPendente, decimal? CustoCorrigido,
    decimal ReceitaLiquida, decimal Resultado,
    double? Roi, double? RoiReal, double? TirAnual, double? Moic, int? PrazoDias,
    IReadOnlyList<string> Avisos, IReadOnlyList<MovimentoFluxo> Fluxo,
    bool IncluiLancamentos = false, decimal SaldoLancamentos = 0, decimal? VendaLiquida = null);

/// <summary>Lançamento financeiro (valor com sinal: receita +, despesa −) para a apuração. Data: liquidação (liquidados) ou vencimento.</summary>
public sealed record LancamentoRetorno(DateOnly Data, decimal Valor, SituacaoLancamento Situacao, string Descricao, string Conta);

public sealed record DadosRetorno(
    Guid ImovelId, CodigoIndice Indice, DateOnly Ate,
    Aquisicao? Aquisicao, IReadOnlyList<AquisicaoItem> Itens,
    IReadOnlyList<LancamentoRetorno> Lancamentos, FechamentoInvestimento? Fechamento, FatoresIndice Fatores,
    bool IncluirLancamentos = true)
{
    /// <summary>
    /// Empreendimento: rateio recebido das matrículas acima, custos das matrículas abaixo e vendas dos lotes (visão
    /// consolidada). Os de origem Lançamento seguem o interruptor <see cref="IncluirLancamentos"/>.
    /// </summary>
    public IReadOnlyList<MovimentoAdicional> Adicionais { get; init; } = [];
    /// <summary>Data final da correção quando não é a do próprio fechamento (consolidado: a última venda, se tudo foi vendido).</summary>
    public DateOnly? DataFinal { get; init; }
}

/// <summary>
/// Fator acumulado F(mês) = Π(1 + v/100) até o mês. A correção de um valor da data tᵢ até t é F(t)/F(tᵢ).
/// Meses fora da série usam o fator do último mês conhecido anterior (antes da série: 1).
/// </summary>
public sealed class FatoresIndice
{
    private readonly List<(int Mes, double Fator)> _pontos = [];

    public FatoresIndice(IEnumerable<(DateOnly Competencia, decimal VariacaoPct)> serie)
    {
        var acc = 1d;
        foreach (var (c, v) in serie.OrderBy(s => s.Competencia))
        {
            acc *= 1 + (double)v / 100;
            _pontos.Add((Mes(c), acc));
        }
    }

    public DateOnly? PrimeiroMes => _pontos.Count == 0 ? null : DeMes(_pontos[0].Mes);
    public DateOnly? UltimoMes => _pontos.Count == 0 ? null : DeMes(_pontos[^1].Mes);

    public double Fator(DateOnly data)
    {
        var mes = Mes(data);
        var f = 1d;
        foreach (var p in _pontos)
        {
            if (p.Mes <= mes) f = p.Fator;
            else break;
        }
        return f;
    }

    internal static int Mes(DateOnly d) => d.Year * 12 + d.Month - 1;
    private static DateOnly DeMes(int m) => new(m / 12, m % 12 + 1, 1);
}

/// <summary>
/// Retorno do investimento, a partir do fluxo de caixa datado:
///  - SAÍDAS: valor pago + itens de aquisição (CAPEX) e, se incluídos, os títulos de despesa liquidados (OPEX);
///  - ENTRADAS: venda líquida do fechamento (venda − comissão − impostos − outros custos) e, se incluídos, os
///    títulos de receita liquidados;
///  - passivo pendente: títulos de despesa abertos ou previstos já vencidos (fora do custo, destacados);
///  - correção: cada saída é multiplicada por F(data final)/F(data da saída) do índice escolhido.
/// Cálculo em centavos (long), como na versão Node, para dar exatamente os mesmos valores.
/// </summary>
public static class CalculoRetorno
{
    private sealed record Mov(DateOnly Data, TipoMovimento Tipo, string Descricao, long Centavos, bool Aquisicao = false);

    public static ResultadoRetorno Calcular(DadosRetorno d)
    {
        var avisos = new List<string>();
        var movs = new List<Mov>();
        var indice = d.Indice.ToString();

        var aq = d.Aquisicao;
        var adicionais = d.Adicionais.Where(m => m.Data <= d.Ate && (d.IncluirLancamentos || m.Origem != OrigemMovimento.Lancamento)).ToList();
        if (aq is null && !adicionais.Any(m => m.Origem == OrigemMovimento.Aquisicao))
            avisos.Add("Aquisição não cadastrada: o custo de aquisição está zerado.");
        else if (aq is not null && aq.DataAquisicao <= d.Ate)
            movs.AddRange(MovimentosAquisicao(aq, d.Itens).Select(m => new Mov(m.Data, m.Tipo, m.Descricao, Centavos(m.Valor), true)));
        foreach (var m in adicionais)
            movs.Add(new Mov(m.Data, m.Tipo, m.Descricao, Centavos(m.Valor), m.Origem == OrigemMovimento.Aquisicao));

        long pendente = 0;
        var realizados = d.Lancamentos.Where(l => l.Situacao == SituacaoLancamento.LIQUIDADO && l.Data <= d.Ate).ToList();
        var saldoLancamentos = realizados.Sum(l => Centavos(l.Valor));
        if (d.IncluirLancamentos)
        {
            foreach (var l in realizados.OrderBy(l => l.Data))
                movs.Add(new Mov(l.Data, l.Valor < 0 ? TipoMovimento.SAIDA : TipoMovimento.ENTRADA, $"{l.Conta}: {l.Descricao}", Math.Abs(Centavos(l.Valor))));
            pendente = -d.Lancamentos.Where(l => (l.Situacao is SituacaoLancamento.ABERTO or SituacaoLancamento.PREVISTO) && l.Valor < 0 && l.Data <= d.Ate).Sum(l => Centavos(l.Valor));
        }

        long? vendaLiquida = null;
        DateOnly? venda = null;
        if (d.Fechamento is { } fe && fe.Data <= d.Ate)
        {
            vendaLiquida = Centavos(fe.ValorVenda) - Centavos(fe.Comissao) - Centavos(fe.Impostos) - Centavos(fe.OutrosCustos);
            venda = fe.Data;
            movs.Add(new Mov(fe.Data, TipoMovimento.ENTRADA, "Venda (líquido de comissão, impostos e custos)", vendaLiquida.Value));
        }

        // ordem cronológica; no mesmo dia, saídas antes de entradas (OrderBy é estável)
        movs = movs.OrderBy(m => m.Data).ThenBy(m => m.Tipo == TipoMovimento.SAIDA ? 0 : 1).ToList();
        var saidas = movs.Where(m => m.Tipo == TipoMovimento.SAIDA).ToList();
        var custo = saidas.Sum(m => m.Centavos);
        var receita = movs.Where(m => m.Tipo == TipoMovimento.ENTRADA).Sum(m => m.Centavos);
        var custoAquisicao = saidas.Where(m => m.Aquisicao).Sum(m => m.Centavos);

        // Data final da correção: a venda (se houver), senão a data de corte.
        var dataFinal = venda ?? d.DataFinal ?? d.Ate;

        long? corrigido = null;
        var corrigidos = new long?[movs.Count];
        var f = d.Fatores;
        if (f.PrimeiroMes is null)
        {
            avisos.Add($"Série do índice {indice} vazia: atualize os índices econômicos (botão \"Atualizar índices\" no Retorno do investimento) para calcular a correção.");
        }
        else
        {
            corrigido = 0;
            var fFim = f.Fator(dataFinal);
            for (var i = 0; i < movs.Count; i++)
            {
                if (movs[i].Tipo != TipoMovimento.SAIDA) continue;
                var c = (long)ArredondarJs(movs[i].Centavos * (fFim / f.Fator(movs[i].Data)));
                corrigidos[i] = c;
                corrigido += c;
            }
            if (saidas.Count > 0 && FatoresIndice.Mes(f.PrimeiroMes.Value) > FatoresIndice.Mes(saidas[0].Data))
                avisos.Add($"A série {indice} começa em {f.PrimeiroMes:MM/yyyy}, depois da primeira saída: a correção anterior a isso não foi aplicada.");
            if (f.UltimoMes is { } ult && FatoresIndice.Mes(ult) < FatoresIndice.Mes(dataFinal))
                avisos.Add($"A série {indice} vai até {ult:MM/yyyy}; meses seguintes sem correção.");
        }

        var temReceita = receita > 0;
        if (!temReceita) avisos.Add("Sem entradas registradas: ROI, TIR e MOIC só são calculados após o fechamento (venda).");
        var resultado = receita - custo;
        var tir = temReceita
            ? Xirr(movs.Select(m => (m.Data, (m.Tipo == TipoMovimento.SAIDA ? -m.Centavos : m.Centavos) / 100d)).ToList())
            : null;
        DateOnly? inicio = saidas.Count > 0 ? saidas[0].Data : null;

        return new ResultadoRetorno(
            d.ImovelId, d.Ate, dataFinal, d.Indice,
            Reais(custoAquisicao), Reais(custo), Reais(pendente), corrigido is null ? null : Reais(corrigido.Value),
            Reais(receita), Reais(resultado),
            Roi: temReceita ? Razao(resultado, custo) : null,
            RoiReal: temReceita && corrigido is not null ? Razao(receita - corrigido.Value, corrigido.Value) : null,
            TirAnual: tir,
            Moic: temReceita ? Razao(receita, custo) : null,
            PrazoDias: inicio is null ? null : dataFinal.DayNumber - inicio.Value.DayNumber,
            avisos,
            movs.Select((m, i) => new MovimentoFluxo(m.Data, m.Tipo, m.Descricao, Reais(m.Centavos), corrigidos[i] is { } c ? Reais(c) : null)).ToList(),
            d.IncluirLancamentos, Reais(saldoLancamentos), vendaLiquida is { } v ? Reais(v) : null);
    }

    /// <summary>Saídas da aquisição: valor pago, itens detalhados e o resíduo de impostos/taxas/despesas sem detalhamento.</summary>
    public static IEnumerable<MovimentoAdicional> MovimentosAquisicao(Aquisicao aq, IEnumerable<AquisicaoItem> itens)
    {
        yield return new(aq.DataAquisicao, TipoMovimento.SAIDA, "Valor pago na aquisição", aq.ValorPago, OrigemMovimento.Aquisicao);
        long detalhado = 0;
        foreach (var it in itens.OrderBy(i => i.CriadoEm))
        {
            detalhado += Centavos(it.Valor);
            var tipo = it.Tipo switch { TipoItemAquisicao.IMPOSTO => "Imposto", TipoItemAquisicao.TAXA => "Taxa", _ => "Despesa" };
            yield return new(it.DataPagamento ?? aq.DataAquisicao, TipoMovimento.SAIDA, $"{tipo} de aquisição: {it.Descricao}", it.Valor, OrigemMovimento.Aquisicao);
        }
        var residual = Centavos(aq.Impostos) + Centavos(aq.Taxas) + Centavos(aq.DespesasDiversas) - detalhado;
        if (residual > 0)
            yield return new(aq.DataAquisicao, TipoMovimento.SAIDA, "Impostos/taxas/despesas de aquisição (sem detalhamento)", Reais(residual), OrigemMovimento.Aquisicao);
    }

    /// <summary>Taxa interna de retorno anualizada (XIRR) por bissecção; null se não há saída e entrada.</summary>
    public static double? Xirr(IReadOnlyList<(DateOnly Data, double Valor)> fluxo)
    {
        if (!fluxo.Any(f => f.Valor < 0) || !fluxo.Any(f => f.Valor > 0)) return null;
        var t0 = fluxo.Min(f => f.Data).DayNumber;
        double Vpl(double r) => fluxo.Sum(f => f.Valor / Math.Pow(1 + r, (f.Data.DayNumber - t0) / 365d));
        double lo = -0.9999, hi = 100;
        if (Vpl(lo) * Vpl(hi) > 0) return null;
        for (var i = 0; i < 200; i++)
        {
            var mid = (lo + hi) / 2;
            if (Vpl(lo) * Vpl(mid) <= 0) hi = mid; else lo = mid;
        }
        return ArredondarJs((lo + hi) / 2 * 1e6) / 1e6;
    }

    private static double? Razao(long a, long b) => b == 0 ? null : ArredondarJs((double)a / b * 1e6) / 1e6;

    /// <summary>Arredondamento igual ao Math.round do JavaScript (meio para cima), para bater com a versão Node.</summary>
    private static double ArredondarJs(double x) => Math.Floor(x + 0.5);

    private static long Centavos(decimal v) => (long)decimal.Round(v * 100, MidpointRounding.AwayFromZero);
    private static decimal Reais(long centavos) => centavos / 100m;
}
