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
/// <summary>Repasse de um locador. <see cref="DescontoManutencao"/>: conserto por conta do locador abatido deste repasse.</summary>
public sealed record RepasseCalculado(Guid PessoaId, decimal Percentual, decimal Bruto, decimal Taxa, decimal Intermediacao, decimal DescontoManutencao = 0)
{
    public decimal Liquido => Bruto - Taxa - Intermediacao - DescontoManutencao;
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
        // dia 29/30 em mês mais curto (fevereiro): vence no último dia do mês
        var venc = new DateOnly(mes.Year, mes.Month, Math.Min(dia, DateTime.DaysInMonth(mes.Year, mes.Month)));
        // adiantado no 1º mês: não vence antes de o contrato começar
        return venc < inicio ? inicio : venc;
    }

    /// <summary>
    /// Itens do mês: aluguel e encargos, proporcionais quando o mês não é inteiro. <paramref name="fim"/> é o último dia
    /// cobrado (padrão: encerramento ou fim do prazo); contrato ativo passa <see cref="FimCobranca"/>.
    /// </summary>
    public static IReadOnlyList<ItemCalculado> Itens(ContratoLocacao c, DateOnly competencia, DateOnly? fim = null)
    {
        var (usados, doMes) = DiasDeUso(competencia, c.Inicio, fim ?? c.EncerradoEm ?? c.Fim);
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
        // conserto cobrado do inquilino (item MANUTENCAO) reembolsa a imobiliária, que pagou o prestador: fica fora do repasse
        var bruto = principal + multa + juros - Reembolso(itens, principal);
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

    /// <summary>Parte do recebimento que reembolsa conserto cobrado do inquilino (proporcional no pagamento parcial).</summary>
    public static decimal Reembolso(IReadOnlyList<FaturaLocacaoItem> itens, decimal principal)
    {
        var manut = itens.Where(i => i.Tipo == TipoItemFatura.MANUTENCAO).Sum(i => i.Valor);
        if (manut <= 0) return 0;
        var total = itens.Sum(i => i.Valor);
        var fracao = total <= 0 ? 1 : Math.Min(1, principal / total);
        return decimal.Round(manut * fracao, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Desconta dos repasses o conserto por conta do locador ainda não abatido (<paramref name="pendente"/>), até zerar o
    /// líquido; o desconto se divide na proporção do líquido de cada locador (a sobra do arredondamento fica com o último).
    /// </summary>
    public static IReadOnlyList<RepasseCalculado> AbaterManutencao(IReadOnlyList<RepasseCalculado> repasses, decimal pendente)
    {
        var disponivel = repasses.Sum(r => Math.Max(0, r.Liquido));
        var total = Math.Min(Math.Max(0, pendente), disponivel);
        if (total <= 0) return repasses;
        var lista = new List<RepasseCalculado>();
        decimal usado = 0;
        for (var i = 0; i < repasses.Count; i++)
        {
            var r = repasses[i];
            var parte = i == repasses.Count - 1
                ? total - usado
                : Math.Min(Math.Max(0, r.Liquido), decimal.Round(total * Math.Max(0, r.Liquido) / disponivel, 2, MidpointRounding.AwayFromZero));
            usado += parte;
            lista.Add(r with { DescontoManutencao = r.DescontoManutencao + parte });
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

    /// <summary>
    /// Último dia cobrado. Ativo: só a entrega das chaves encerra a cobrança; vencido o prazo, a locação segue por prazo
    /// indeterminado (arts. 46 e 56) e o mês do fim do prazo é cobrado inteiro.
    /// </summary>
    public static DateOnly FimCobranca(ContratoLocacao c) => c.EncerradoEm ?? DateOnly.MaxValue;

    /* ------------------------------ reajuste ------------------------------ */

    /// <summary>Meses do índice usados no reajuste: os 12 que terminam no mês anterior ao aniversário.</summary>
    public static (DateOnly De, DateOnly Ate) PeriodoReajuste(DateOnly aniversario)
    {
        var ate = MesDe(aniversario).AddMonths(-1);
        return (ate.AddMonths(-11), ate);
    }

    /// <summary>
    /// Índice acumulado (produto das variações mensais) e o novo aluguel. Acumulado negativo mantém o valor: o contrato
    /// fala em reajuste pela inflação, não em redução.
    /// </summary>
    public static (decimal Percentual, decimal Novo) Reajuste(decimal valor, IEnumerable<decimal> variacoesMensaisPct)
    {
        var fator = variacoesMensaisPct.Aggregate(1m, (f, v) => f * (1 + v / 100));
        var pct = decimal.Round((fator - 1) * 100, 4, MidpointRounding.AwayFromZero);
        var novo = pct <= 0 ? valor : decimal.Round(valor * fator, 2, MidpointRounding.AwayFromZero);
        return (pct, novo);
    }

    /* ------------------------------ rescisão ------------------------------ */

    /// <summary>
    /// Acerto da rescisão (o aluguel até a entrega fica na fatura do mês, pró-rata):
    /// - multa do art. 4: locatário saindo antes do fim do prazo paga a multa do contrato proporcional ao tempo que falta;
    /// - aviso prévio do art. 6: no prazo indeterminado, o locatário avisa com 30 dias; o que faltar de aviso é cobrado em
    ///   aluguel e encargos proporcionais;
    /// - débitos da vistoria de saída e outros encargos;
    /// - caução (art. 38) abate os débitos; o que sobrar volta ao locatário.
    /// </summary>
    public static RescisaoCalculo Rescisao(ContratoLocacao c, RescisaoForm f)
    {
        var entrega = f.EntregaChaves!.Value;
        var itens = new List<ItemCalculado>();
        var sairPorConta = f.Iniciativa == IniciativaRescisao.LOCATARIO && !f.IsentarMulta;

        int diasRestantes = 0, diasPrazo = c.Fim.DayNumber - c.Inicio.DayNumber + 1;
        decimal multa = 0;
        if (entrega < c.Fim)
        {
            diasRestantes = c.Fim.DayNumber - entrega.DayNumber;
            if (sairPorConta && c.MultaRescisoriaAlugueis > 0)
            {
                multa = decimal.Round(c.MultaRescisoriaAlugueis * c.ValorAluguel * diasRestantes / diasPrazo, 2, MidpointRounding.AwayFromZero);
                if (multa > 0)
                    itens.Add(new(TipoItemFatura.MULTA_RESCISORIA,
                        $"Multa rescisória: {c.MultaRescisoriaAlugueis.ToString("0.#", Comum.Texto.PtBr)} aluguéis × {diasRestantes} de {diasPrazo} dias restantes (art. 4)", multa));
            }
        }

        int faltando = 0;
        decimal aviso = 0;
        if (sairPorConta && entrega > c.Fim)
        {
            var diasAviso = f.AvisoEm is { } a ? Math.Max(0, entrega.DayNumber - a.DayNumber) : 0;
            faltando = Math.Clamp(30 - diasAviso, 0, 30);
            var mensal = c.ValorAluguel + c.ValorCondominio + c.ValorIptu + c.ValorSeguroIncendio;
            aviso = decimal.Round(mensal * faltando / 30, 2, MidpointRounding.AwayFromZero);
            if (aviso > 0)
                itens.Add(new(TipoItemFatura.AVISO_PREVIO, $"Aviso prévio não cumprido: {faltando} de 30 dias (art. 6)", aviso));
        }

        if (f.DebitosVistoria > 0)
            itens.Add(new(TipoItemFatura.VISTORIA, Comum.Texto.Limpo(f.DescricaoVistoria) ?? "Débitos da vistoria de saída", f.DebitosVistoria));
        if (f.OutrosDebitos > 0)
            itens.Add(new(TipoItemFatura.OUTRO, Comum.Texto.Limpo(f.DescricaoOutros) ?? "Encargos pendentes", f.OutrosDebitos));

        decimal caucao = 0, abatida = 0;
        if (c.Garantia == GarantiaLocacao.CAUCAO)
        {
            caucao = f.CaucaoCorrigida ?? c.GarantiaValor ?? 0;
            abatida = Math.Min(caucao, itens.Sum(i => i.Valor));
            if (abatida > 0) itens.Add(new(TipoItemFatura.CAUCAO, "Caução abatida dos débitos", -abatida));
        }
        return new(diasRestantes, diasPrazo, multa, faltando, aviso, itens, caucao, abatida);
    }

    /* ------------------------------ vencimentos ------------------------------ */

    /// <summary>Eventos do contrato ativo que pedem providência, com as antecedências (dias) em que avisar.</summary>
    public static IReadOnlyList<(TipoAvisoLocacao Tipo, DateOnly Data, int[] Antecedencias)> Eventos(ContratoLocacao c)
    {
        if (c.Status != StatusContratoLocacao.ATIVO) return [];
        var lista = new List<(TipoAvisoLocacao, DateOnly, int[])>
        {
            (TipoAvisoLocacao.REAJUSTE, c.ProximoReajuste, [60, 30]),
            (TipoAvisoLocacao.FIM, c.Fim, [60, 30]),
        };
        if (c.GarantiaVence is { } g) lista.Add((TipoAvisoLocacao.GARANTIA, g, [60, 30]));
        if (c.SeguroIncendioVence is { } si) lista.Add((TipoAvisoLocacao.SEGURO_INCENDIO, si, [30]));
        if (c.PreferenciaPrazo is { } pp && c.PreferenciaResposta is null) lista.Add((TipoAvisoLocacao.PREFERENCIA, pp, [7]));
        return lista;
    }

    /// <summary>
    /// Avisos que valem hoje: a menor antecedência já alcançada de cada evento (um aviso aberto por vez). Passados 30
    /// dias da data, o aviso deixa de valer, menos o do reajuste, que espera ser aplicado.
    /// </summary>
    public static IReadOnlyList<(TipoAvisoLocacao Tipo, DateOnly Data, int Antecedencia)> AvisosDevidos(ContratoLocacao c, DateOnly hoje)
    {
        var lista = new List<(TipoAvisoLocacao, DateOnly, int)>();
        foreach (var (tipo, data, ants) in Eventos(c))
        {
            if (tipo != TipoAvisoLocacao.REAJUSTE && hoje > data.AddDays(30)) continue;
            var alcancadas = ants.Where(a => hoje >= data.AddDays(-a)).ToList();
            if (alcancadas.Count > 0) lista.Add((tipo, data, alcancadas.Min()));
        }
        return lista;
    }
}
