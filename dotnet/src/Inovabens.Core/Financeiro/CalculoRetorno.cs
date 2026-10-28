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
    IReadOnlyList<string> Avisos, IReadOnlyList<MovimentoFluxo> Fluxo);

public sealed record LancamentoRetorno(
    DateOnly Competencia, DateOnly? Vencimento, DateOnly? DataPagamento, StatusLancamento? Status,
    decimal Valor, string Descricao, string Categoria);

public sealed record DadosRetorno(
    Guid ImovelId, CodigoIndice Indice, DateOnly Ate,
    Aquisicao? Aquisicao, IReadOnlyList<AquisicaoItem> Itens,
    IReadOnlyList<LancamentoRetorno> Lancamentos, IReadOnlyList<Receita> Receitas, FatoresIndice Fatores);

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
/// Retorno do investimento (port fiel de retorno.service.ts), a partir do fluxo de caixa datado:
///  - SAÍDAS: valor pago + itens de aquisição + despesas PAGAS (OPEX);
///  - ENTRADAS: receitas líquidas (venda/locação), já descontados custos de venda e tributos;
///  - passivo pendente: despesas NAO_PAGO (fora do custo, destacadas);
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
        if (aq is null) avisos.Add("Aquisição não cadastrada: o custo de aquisição está zerado.");
        else if (aq.DataAquisicao <= d.Ate)
        {
            movs.Add(new Mov(aq.DataAquisicao, TipoMovimento.SAIDA, "Valor pago na aquisição", Centavos(aq.ValorPago), true));
            long detalhado = 0;
            foreach (var it in d.Itens.OrderBy(i => i.CriadoEm))
            {
                var c = Centavos(it.Valor);
                detalhado += c;
                var tipo = it.Tipo switch { TipoItemAquisicao.IMPOSTO => "Imposto", TipoItemAquisicao.TAXA => "Taxa", _ => "Despesa" };
                movs.Add(new Mov(it.DataPagamento ?? aq.DataAquisicao, TipoMovimento.SAIDA, $"{tipo} de aquisição: {it.Descricao}", c, true));
            }
            var residual = Centavos(aq.Impostos) + Centavos(aq.Taxas) + Centavos(aq.DespesasDiversas) - detalhado;
            if (residual > 0)
                movs.Add(new Mov(aq.DataAquisicao, TipoMovimento.SAIDA, "Impostos/taxas/despesas de aquisição (sem detalhamento)", residual, true));
        }

        long pendente = 0;
        foreach (var l in d.Lancamentos.Where(l => l.Competencia <= d.Ate))
        {
            if (l.Status == StatusLancamento.NAO_PAGO) pendente += Centavos(l.Valor);
            if (l.Status == StatusLancamento.PAGO)
            {
                var data = l.DataPagamento ?? l.Vencimento ?? l.Competencia;
                if (data <= d.Ate)
                    movs.Add(new Mov(data, TipoMovimento.SAIDA, $"{l.Categoria}: {l.Descricao} ({l.Competencia:MM/yyyy})", Centavos(l.Valor)));
            }
        }

        var receitas = d.Receitas.Where(r => r.Data <= d.Ate).ToList();
        foreach (var r in receitas)
        {
            var tipo = r.Tipo switch { TipoReceita.VENDA => "Venda", TipoReceita.LOCACAO => "Locação", _ => "Outra receita" };
            movs.Add(new Mov(r.Data, TipoMovimento.ENTRADA, $"{tipo} (líquido)", Centavos(r.ValorLiquido)));
        }

        // ordem cronológica; no mesmo dia, saídas antes de entradas (OrderBy é estável)
        movs = movs.OrderBy(m => m.Data).ThenBy(m => m.Tipo == TipoMovimento.SAIDA ? 0 : 1).ToList();
        var saidas = movs.Where(m => m.Tipo == TipoMovimento.SAIDA).ToList();
        var custo = saidas.Sum(m => m.Centavos);
        var receita = movs.Where(m => m.Tipo == TipoMovimento.ENTRADA).Sum(m => m.Centavos);
        var custoAquisicao = saidas.Where(m => m.Aquisicao).Sum(m => m.Centavos);

        // Data final da correção: a venda (se houver), senão a data de corte.
        var venda = receitas.Where(r => r.Tipo == TipoReceita.VENDA).Select(r => (DateOnly?)r.Data).Max();
        var dataFinal = venda ?? d.Ate;

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
        if (!temReceita) avisos.Add("Sem receitas registradas: ROI, TIR e MOIC só são calculados após a venda/locação.");
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
            movs.Select((m, i) => new MovimentoFluxo(m.Data, m.Tipo, m.Descricao, Reais(m.Centavos), corrigidos[i] is { } c ? Reais(c) : null)).ToList());
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
