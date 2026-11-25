using Inovabens.Core.Dominio;

namespace Inovabens.Core.Locacao;

/// <summary>Valor de um item da fatura antes de gravar.</summary>
public sealed record ItemCalculado(TipoItemFatura Tipo, string Descricao, decimal Valor);

/// <summary>Multa e juros de um recebimento em atraso.</summary>
public sealed record Acrescimos(int DiasAtraso, decimal Multa, decimal Juros)
{
    public decimal Total => Multa + Juros;
}

/// <summary>Parte de um locador num recebimento.</summary>
public sealed record RepasseCalculado(Guid PessoaId, decimal Percentual, decimal Bruto, decimal Taxa, decimal Intermediacao)
{
    public decimal Liquido => Bruto - Taxa - Intermediacao;
}

/// <summary>
/// Regras de cálculo da locação, sem banco (testáveis isoladamente). Aluguel por mês civil: a competência é o mês de
/// uso e vence no dia escolhido do mês seguinte (Lei 8.245, art. 20, proíbe cobrar adiantado, salvo sem garantia ou
/// temporada). Primeiro e último mês proporcionais aos dias de uso.
/// </summary>
public static class CalculoLocacao
{
    /// <summary>Meses de uso do contrato (dia 1 de cada), do mês do início ao mês do fim.</summary>
    public static IReadOnlyList<DateOnly> Competencias(DateOnly inicio, DateOnly fim)
    {
        var lista = new List<DateOnly>();
        for (var m = MesDe(inicio); m <= fim; m = m.AddMonths(1)) lista.Add(m);
        return lista;
    }

    /// <summary>Dias de uso no mês e dias do mês (para o pró-rata).</summary>
    public static (int Usados, int DoMes) DiasDeUso(DateOnly competencia, DateOnly inicio, DateOnly fim)
    {
        var diasMes = DateTime.DaysInMonth(competencia.Year, competencia.Month);
        var ultimo = competencia.AddDays(diasMes - 1);
        var de = inicio > competencia ? inicio : competencia;
        var ate = fim < ultimo ? fim : ultimo;
        var usados = ate < de ? 0 : ate.DayNumber - de.DayNumber + 1;
        return (usados, diasMes);
    }

    public static DateOnly Vencimento(DateOnly competencia, short dia, bool antecipado, DateOnly inicio)
    {
        var mes = antecipado ? competencia : competencia.AddMonths(1);
        var venc = new DateOnly(mes.Year, mes.Month, dia);
        // adiantado no 1º mês: não vence antes de o contrato começar
        return venc < inicio ? inicio : venc;
    }

    /// <summary>Itens do mês: aluguel e encargos, proporcionais quando o mês não é inteiro.</summary>
    public static IReadOnlyList<ItemCalculado> Itens(ContratoLocacao c, DateOnly competencia)
    {
        var (usados, doMes) = DiasDeUso(competencia, c.Inicio, c.Fim);
        if (usados == 0) return [];
        var inteiro = usados == doMes;
        decimal Parte(decimal v) => inteiro ? v : decimal.Round(v * usados / doMes, 2, MidpointRounding.AwayFromZero);
        var ref_ = $"{competencia:MM/yyyy}" + (inteiro ? "" : $" ({usados} de {doMes} dias)");
        var itens = new List<ItemCalculado> { new(TipoItemFatura.ALUGUEL, $"Aluguel {ref_}", Parte(c.ValorAluguel)) };
        if (c.ValorCondominio > 0) itens.Add(new(TipoItemFatura.CONDOMINIO, $"Condomínio {ref_}", Parte(c.ValorCondominio)));
        if (c.ValorIptu > 0) itens.Add(new(TipoItemFatura.IPTU, $"IPTU {ref_}", Parte(c.ValorIptu)));
        if (c.ValorSeguroIncendio > 0) itens.Add(new(TipoItemFatura.SEGURO_INCENDIO, $"Seguro incêndio {ref_}", Parte(c.ValorSeguroIncendio)));
        return itens;
    }

    /// <summary>
    /// Multa e juros simples pro rata die (juros do mês ÷ 30 por dia). Vencimento em sábado ou domingo passa para a
    /// segunda-feira sem acréscimo (feriados não são considerados: ajuste manual no recebimento).
    /// </summary>
    public static Acrescimos Atraso(decimal principal, DateOnly vencimento, DateOnly pagamento, decimal multaPct, decimal jurosMesPct)
    {
        var limite = vencimento.DayOfWeek switch
        {
            DayOfWeek.Saturday => vencimento.AddDays(2),
            DayOfWeek.Sunday => vencimento.AddDays(1),
            _ => vencimento,
        };
        if (pagamento <= limite || principal <= 0) return new(0, 0, 0);
        var dias = pagamento.DayNumber - vencimento.DayNumber;
        var multa = decimal.Round(principal * multaPct / 100, 2, MidpointRounding.AwayFromZero);
        var juros = decimal.Round(principal * jurosMesPct / 100 / 30 * dias, 2, MidpointRounding.AwayFromZero);
        return new(dias, multa, juros);
    }

    /// <summary>
    /// Divide um recebimento entre os locadores. A taxa de administração incide sobre o aluguel (com descontos), a multa
    /// e os juros; encargos (condomínio, IPTU, seguro, outros) passam inteiros. A intermediação (padrão: 1º aluguel)
    /// é retida dos primeiros recebimentos até completar <paramref name="intermediacaoPendente"/>, e a parte retida não
    /// paga taxa de administração.
    /// </summary>
    /// <param name="itens">Itens da fatura (o recebimento pode ser parcial: rateado na proporção do principal).</param>
    public static IReadOnlyList<RepasseCalculado> Repasses(IReadOnlyList<(Guid PessoaId, decimal Percentual)> locadores,
        IReadOnlyList<FaturaLocacaoItem> itens, decimal principal, decimal multa, decimal juros, decimal taxaPct,
        decimal intermediacaoPendente)
    {
        if (locadores.Count == 0) return [];
        var totalFatura = itens.Sum(i => i.Valor);
        var fracao = totalFatura <= 0 ? 1 : Math.Min(1, principal / totalFatura);
        var aluguel = itens.Where(i => i.Tipo is TipoItemFatura.ALUGUEL or TipoItemFatura.DESCONTO).Sum(i => i.Valor) * fracao;
        aluguel = Math.Max(0, decimal.Round(aluguel, 2, MidpointRounding.AwayFromZero));
        var bruto = principal + multa + juros;
        var intermediacao = Math.Min(Math.Max(0, intermediacaoPendente), aluguel);
        var baseTaxa = aluguel - intermediacao + multa + juros;
        var taxa = decimal.Round(baseTaxa * taxaPct / 100, 2, MidpointRounding.AwayFromZero);

        // rateio pelos percentuais; a sobra do arredondamento fica com o último
        var somaPct = locadores.Sum(l => l.Percentual);
        var lista = new List<RepasseCalculado>();
        decimal rb = 0, rt = 0, ri = 0;
        for (var i = 0; i < locadores.Count; i++)
        {
            var (pessoa, pct) = locadores[i];
            if (i == locadores.Count - 1)
            {
                lista.Add(new(pessoa, pct, bruto - rb, taxa - rt, intermediacao - ri));
                break;
            }
            var f = somaPct == 0 ? 0 : pct / somaPct;
            var b = decimal.Round(bruto * f, 2, MidpointRounding.AwayFromZero);
            var t = decimal.Round(taxa * f, 2, MidpointRounding.AwayFromZero);
            var m = decimal.Round(intermediacao * f, 2, MidpointRounding.AwayFromZero);
            lista.Add(new(pessoa, pct, b, t, m));
            rb += b; rt += t; ri += m;
        }
        return lista;
    }

    /// <summary>Data do repasse: <paramref name="dias"/> dias depois do recebimento, passando fim de semana para segunda.</summary>
    public static DateOnly DataRepasse(DateOnly recebimento, int dias)
    {
        var d = recebimento.AddDays(dias);
        return d.DayOfWeek switch { DayOfWeek.Saturday => d.AddDays(2), DayOfWeek.Sunday => d.AddDays(1), _ => d };
    }

    public static DateOnly MesDe(DateOnly d) => new(d.Year, d.Month, 1);
}
