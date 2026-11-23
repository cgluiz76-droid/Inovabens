using Inovabens.Core.Dominio;

namespace Inovabens.Core.Financeiro;

/// <summary>De onde vem um movimento do fluxo: custo de aquisição (CAPEX), lançamento financeiro (OPEX) ou venda.</summary>
public enum OrigemMovimento { Aquisicao, Lancamento, Venda }

/// <summary>Movimento do fluxo de caixa vindo de fora do próprio imóvel (rateio do empreendimento, custo de outra matrícula da árvore, venda de um lote).</summary>
public sealed record MovimentoAdicional(DateOnly Data, TipoMovimento Tipo, string Descricao, decimal Valor, OrigemMovimento Origem);

/// <summary>Matrícula da árvore do empreendimento: o empreendimento (sem pai), um lote ou uma área remanescente.</summary>
public sealed record NoRateio(Guid Id, Guid? PaiId, string Codigo, decimal? AreaM2, DateOnly? VendaData);

/// <summary>Custo (ou receita) lançado numa matrícula da árvore.</summary>
public sealed record CustoNo(Guid NoId, MovimentoAdicional Mov);

public sealed class ResultadoRateio
{
    /// <summary>O que cada matrícula recebeu das matrículas acima dela (os lotes e as remanescentes intermediárias).</summary>
    public Dictionary<Guid, List<MovimentoAdicional>> Recebidos { get; } = [];
    /// <summary>Custos que não tinham para onde ir (todas as unidades abaixo já vendidas na data): ficam no empreendimento.</summary>
    public List<CustoNo> NaoRateados { get; } = [];
    public List<string> Avisos { get; } = [];

    public IReadOnlyList<MovimentoAdicional> De(Guid id) => Recebidos.TryGetValue(id, out var l) ? l : [];
}

/// <summary>
/// Rateio hierárquico do custo de um empreendimento. Regra única: o custo lançado numa matrícula se divide, pela área,
/// entre as matrículas que saíram dela e que ainda estão ativas na data do custo; uma remanescente desmembrada repassa
/// o que recebeu aos filhos, do mesmo jeito. Ativa = lote não vendido até aquela data (o custo do lote vendido fica
/// congelado no fechamento) ou remanescente com alguma unidade ativa abaixo. A área doada ao município não recebe
/// rateio: o custo dela fica com quem foi vendido (é parte do custo de produzir os lotes).
/// Tudo é recalculado a partir dos lançamentos, sem transferência manual de valores. Cálculo em centavos.
/// </summary>
public static class RateioEmpreendimento
{
    public static ResultadoRateio Calcular(IReadOnlyList<NoRateio> nos, IEnumerable<CustoNo> custos)
    {
        var r = new ResultadoRateio();
        var porId = nos.ToDictionary(n => n.Id);
        var filhos = nos.Where(n => n.PaiId is { } p && porId.ContainsKey(p))
            .GroupBy(n => n.PaiId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Codigo, StringComparer.Ordinal).ToList());
        var avisados = new HashSet<Guid>();

        bool Ativo(NoRateio n, DateOnly data) => filhos.TryGetValue(n.Id, out var f)
            ? f.Any(x => Ativo(x, data))
            : n.VendaData is not { } v || data <= v;

        foreach (var c in custos.OrderBy(c => c.Mov.Data))
        {
            if (!porId.TryGetValue(c.NoId, out var origem) || !filhos.ContainsKey(origem.Id)) continue; // matrícula sem filhos: o custo é dela
            var centavos = Centavos(c.Mov.Valor);
            if (centavos == 0) continue;
            if (!Distribuir(origem, centavos))
                r.NaoRateados.Add(c);

            bool Distribuir(NoRateio no, long valor)
            {
                var ativos = filhos[no.Id].Where(f => Ativo(f, c.Mov.Data)).ToList();
                if (ativos.Count == 0) return false;
                var semArea = ativos.Any(a => a.AreaM2 is not > 0);
                if (semArea && avisados.Add(no.Id))
                    r.Avisos.Add($"Unidades de {no.Codigo} sem área: o custo foi dividido em partes iguais entre elas. Preencha a área na aba Lotes.");
                var pesos = ativos.Select(a => semArea ? 1m : a.AreaM2!.Value).ToList();
                var total = pesos.Sum();
                long distribuido = 0;
                for (var i = 0; i < ativos.Count; i++)
                {
                    // o último fica com o resto do arredondamento: a soma das partes é exatamente o valor
                    var parte = i == ativos.Count - 1 ? valor - distribuido : (long)decimal.Floor(valor * pesos[i] / total);
                    distribuido += parte;
                    if (parte == 0) continue;
                    var filho = ativos[i];
                    var pct = (decimal)parte / centavos * 100;
                    var mov = c.Mov with
                    {
                        Valor = parte / 100m,
                        Descricao = $"Rateio de {origem.Codigo} ({pct:0.##}%): {c.Mov.Descricao}",
                    };
                    if (!r.Recebidos.TryGetValue(filho.Id, out var lista)) r.Recebidos[filho.Id] = lista = [];
                    lista.Add(mov);
                    if (filhos.ContainsKey(filho.Id)) Distribuir(filho, parte);
                }
                return true;
            }
        }
        return r;
    }

    /// <summary>Matrícula e todas as que saíram dela, em qualquer nível.</summary>
    public static HashSet<Guid> Subarvore(IReadOnlyList<NoRateio> nos, Guid id)
    {
        var resultado = new HashSet<Guid> { id };
        var mudou = true;
        while (mudou)
        {
            mudou = false;
            foreach (var n in nos)
                if (n.PaiId is { } p && resultado.Contains(p) && resultado.Add(n.Id)) mudou = true;
        }
        return resultado;
    }

    private static long Centavos(decimal v) => (long)decimal.Round(Math.Abs(v) * 100, MidpointRounding.AwayFromZero);
}
