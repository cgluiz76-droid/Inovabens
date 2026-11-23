using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Financeiro;

/// <summary>Resultado de uma unidade vendável (lote ou remanescente ainda não desmembrada) no painel do empreendimento.</summary>
public sealed record UnidadeResultado(Guid Id, string Codigo, int Fase, bool Remanescente, decimal? AreaM2, decimal? Preco,
    decimal Custo, decimal ReceitasLancamentos, FechamentoInvestimento? Venda, IReadOnlyList<FechamentoRecebimento> Recebimentos)
{
    public bool Vendida => Venda is not null;
    public decimal VendaLiquida => Venda is { } v ? v.ValorVenda - v.Comissao - v.Impostos - v.OutrosCustos : 0;
    /// <summary>Lucro realizado (só vendidas): venda líquida + receitas − custo (próprio + rateado).</summary>
    public decimal? Resultado => Vendida ? VendaLiquida + ReceitasLancamentos - Custo : null;

    /// <summary>Como a venda foi paga. Fechamento sem formas cadastradas = tudo em dinheiro.</summary>
    public decimal EmDinheiro => !Vendida ? 0 : Recebimentos.Count == 0 ? Venda!.ValorVenda : Recebimentos.Where(r => r.Tipo == TipoRecebimento.DINHEIRO).Sum(r => r.Valor);
    public decimal AReceber => Recebimentos.Where(r => r.Tipo == TipoRecebimento.PARCELADO).Sum(r => r.Valor);
    public decimal EmBens => Recebimentos.Where(r => r.Tipo is TipoRecebimento.PERMUTA_IMOVEL or TipoRecebimento.OUTRO_BEM).Sum(r => r.Valor);
    public decimal TornaPaga => Recebimentos.Sum(r => r.TornaPaga);
}

/// <summary>Soma de um grupo de unidades (uma fase ou o empreendimento todo).</summary>
public sealed record ResumoEmpreendimento(string Titulo, IReadOnlyList<UnidadeResultado> Unidades, decimal CustoNaoRateado = 0)
{
    public int Vendidas => Unidades.Count(u => u.Vendida);
    /// <summary>VGV: vendidas pelo valor de venda, as demais pelo preço anunciado.</summary>
    public decimal Vgv => Unidades.Sum(u => u.Venda?.ValorVenda ?? u.Preco ?? 0);
    public decimal Vendido => Unidades.Sum(u => u.Venda?.ValorVenda ?? 0);
    public decimal CustoVendidas => Unidades.Where(u => u.Vendida).Sum(u => u.Custo);
    /// <summary>Custo das unidades ainda não vendidas (estoque) mais o que não tinha unidade ativa para receber.</summary>
    public decimal CustoEstoque => Unidades.Where(u => !u.Vendida).Sum(u => u.Custo) + CustoNaoRateado;
    public decimal CustoTotal => CustoVendidas + CustoEstoque;
    public decimal Resultado => Unidades.Sum(u => u.Resultado ?? 0);
    public double? Roi => CustoVendidas == 0 ? null : (double)(Resultado / CustoVendidas);
    public decimal EmDinheiro => Unidades.Sum(u => u.EmDinheiro);
    public decimal AReceber => Unidades.Sum(u => u.AReceber);
    public decimal EmBens => Unidades.Sum(u => u.EmBens);
    public decimal TornaPaga => Unidades.Sum(u => u.TornaPaga);
}

public sealed record PainelEmpreendimento(string Codigo, IReadOnlyList<ResumoEmpreendimento> Fases, ResumoEmpreendimento Total,
    IReadOnlyList<string> Avisos);

/// <summary>
/// A árvore de matrículas de um empreendimento com os custos de cada uma e o rateio já feito. Base do retorno de cada
/// lote (custo próprio + rateado), da ficha consolidada do empreendimento e do painel por fase.
/// </summary>
public sealed class ArvoreFinanceira
{
    public required Imovel Raiz { get; init; }
    public required IReadOnlyList<NoRateio> Nos { get; init; }
    public required IReadOnlyDictionary<Guid, Imovel> Imoveis { get; init; }
    /// <summary>Custos e receitas lançados em cada matrícula (aquisição + lançamentos liquidados até o corte).</summary>
    public required IReadOnlyDictionary<Guid, List<MovimentoAdicional>> Proprios { get; init; }
    public required IReadOnlyDictionary<Guid, FechamentoInvestimento> Vendas { get; init; }
    public required IReadOnlyDictionary<Guid, List<FechamentoRecebimento>> Recebimentos { get; init; }
    public required IReadOnlyDictionary<Guid, int> FaseDe { get; init; }
    public required ResultadoRateio Rateio { get; init; }

    public bool TemFilhos(Guid id) => Nos.Any(n => n.PaiId == id);

    /// <summary>
    /// O que entra no retorno da matrícula além dos dados dela: o rateio recebido de cima e, se ela foi desmembrada,
    /// os custos e as vendas das matrículas abaixo (visão consolidada). Data final: a última venda, se tudo foi vendido.
    /// </summary>
    public (IReadOnlyList<MovimentoAdicional> Adicionais, DateOnly? DataFinal) ParaRetorno(Guid id, DateOnly corte)
    {
        var lista = new List<MovimentoAdicional>(Rateio.De(id));
        if (!TemFilhos(id)) return (lista, null);
        var folhas = new List<Guid>();
        foreach (var d in RateioEmpreendimento.Subarvore(Nos, id).Where(x => x != id))
        {
            if (Proprios.TryGetValue(d, out var p)) lista.AddRange(p.Select(m => m with { Descricao = $"{Imoveis[d].Codigo} · {m.Descricao}" }));
            if (TemFilhos(d)) continue;
            folhas.Add(d);
            if (Vendas.TryGetValue(d, out var v) && v.Data <= corte)
                lista.Add(new(v.Data, TipoMovimento.ENTRADA, $"Venda de {Imoveis[d].Codigo} (líquida)",
                    v.ValorVenda - v.Comissao - v.Impostos - v.OutrosCustos, OrigemMovimento.Venda));
        }
        DateOnly? fim = folhas.Count > 0 && folhas.All(f => Vendas.TryGetValue(f, out var v) && v.Data <= corte)
            ? folhas.Max(f => Vendas[f].Data) : null;
        return (lista, fim);
    }

    /// <summary>Painel: resultado de cada unidade vendável, por fase e no total (com o saldo dos lançamentos).</summary>
    public PainelEmpreendimento Painel()
    {
        var unidades = Nos.Where(n => n.PaiId is not null && !TemFilhos(n.Id)).Select(n =>
        {
            var im = Imoveis[n.Id];
            var movs = (Proprios.TryGetValue(n.Id, out var p) ? p : []).Concat(Rateio.De(n.Id)).ToList();
            return new UnidadeResultado(n.Id, n.Codigo, FaseDe.GetValueOrDefault(n.Id), im.UnidadeTipo == TipoUnidade.Remanescente,
                n.AreaM2, im.PrecoVenda,
                movs.Where(m => m.Tipo == TipoMovimento.SAIDA).Sum(m => m.Valor),
                movs.Where(m => m.Tipo == TipoMovimento.ENTRADA && m.Origem == OrigemMovimento.Lancamento).Sum(m => m.Valor),
                Vendas.GetValueOrDefault(n.Id), Recebimentos.TryGetValue(n.Id, out var r) ? r : []);
        }).OrderBy(u => u.Fase).ThenBy(u => u.Remanescente).ThenBy(u => u.Codigo, StringComparer.Ordinal).ToList();

        var naoRateado = Rateio.NaoRateados.Where(c => c.Mov.Tipo == TipoMovimento.SAIDA).Sum(c => c.Mov.Valor);
        var avisos = new List<string>(Rateio.Avisos);
        if (naoRateado > 0)
            avisos.Add($"{Comum.Texto.Moeda(naoRateado)} em custos lançados depois de todas as unidades abaixo da matrícula terem sido vendidas: ficam no custo do empreendimento, fora dos lotes.");
        var fases = unidades.GroupBy(u => u.Fase).OrderBy(g => g.Key).Select(g => new ResumoEmpreendimento($"Fase {g.Key}", g.ToList())).ToList();
        return new(Raiz.Codigo, fases, new ResumoEmpreendimento("Empreendimento", unidades, naoRateado), avisos);
    }

    /// <summary>Árvore do empreendimento a que o imóvel pertence; null se ele não é empreendimento nem unidade de um.</summary>
    public static async Task<ArvoreFinanceira?> CarregarAsync(InovabensDb db, Imovel im, DateOnly corte)
    {
        var raizId = im.EmpreendimentoId ?? im.Id;
        var unidades = await db.Imoveis.AsNoTracking().Where(i => i.EmpreendimentoId == raizId && i.ExcluidoEm == null).ToListAsync();
        if (unidades.Count == 0) return null;
        var raiz = raizId == im.Id ? im : await db.Imoveis.AsNoTracking().SingleAsync(i => i.Id == raizId);
        var todos = unidades.Prepend(raiz).ToDictionary(i => i.Id);
        var ids = todos.Keys.ToList();

        var aquisicoes = await db.Aquisicoes.AsNoTracking().Where(a => ids.Contains(a.ImovelId)).ToListAsync();
        var aqIds = aquisicoes.Select(a => a.Id).ToList();
        var itens = (await db.AquisicaoItens.AsNoTracking().Where(i => aqIds.Contains(i.AquisicaoId)).ToListAsync()).ToLookup(i => i.AquisicaoId);
        var lancs = await db.LancamentosFinanceiros.AsNoTracking()
            .Where(l => ids.Contains(l.ImovelId) && l.ExcluidoEm == null && l.Situacao == SituacaoLancamento.LIQUIDADO)
            .Select(l => new { l.ImovelId, Data = l.DataLiquidacao ?? l.Data, l.Valor, l.Descricao, Conta = l.Conta!.Nome })
            .ToListAsync();
        var vendas = await db.FechamentosInvestimento.AsNoTracking().Where(f => ids.Contains(f.ImovelId)).ToDictionaryAsync(f => f.ImovelId);
        var recebimentos = (await db.FechamentoRecebimentos.AsNoTracking().Where(r => ids.Contains(r.ImovelId)).OrderBy(r => r.Ordem).ToListAsync())
            .GroupBy(r => r.ImovelId).ToDictionary(g => g.Key, g => g.ToList());
        var fases = await db.Desmembramentos.AsNoTracking().Where(d => d.EmpreendimentoId == raizId).ToDictionaryAsync(d => d.Id, d => d.Fase);

        var proprios = new Dictionary<Guid, List<MovimentoAdicional>>();
        List<MovimentoAdicional> De(Guid id) => proprios.TryGetValue(id, out var l) ? l : proprios[id] = [];
        foreach (var a in aquisicoes.Where(a => a.DataAquisicao <= corte))
            De(a.ImovelId).AddRange(CalculoRetorno.MovimentosAquisicao(a, itens[a.Id]).Where(m => m.Data <= corte));
        foreach (var l in lancs.Where(l => l.Data <= corte && l.Valor != 0))
            De(l.ImovelId).Add(new(l.Data, l.Valor < 0 ? TipoMovimento.SAIDA : TipoMovimento.ENTRADA,
                $"{l.Conta}: {l.Descricao}", Math.Abs(l.Valor), OrigemMovimento.Lancamento));

        var nos = todos.Values.Select(i => new NoRateio(i.Id, i.Id == raizId ? null : i.OrigemId, i.Codigo, i.AreaTerrenoM2,
            vendas.TryGetValue(i.Id, out var v) ? v.Data : null)).ToList();
        var custos = proprios.SelectMany(kv => kv.Value.Select(m => new CustoNo(kv.Key, m)));
        return new ArvoreFinanceira
        {
            Raiz = raiz, Nos = nos, Imoveis = todos, Proprios = proprios, Vendas = vendas, Recebimentos = recebimentos,
            FaseDe = unidades.Where(u => u.DesmembramentoId is not null).ToDictionary(u => u.Id, u => fases.GetValueOrDefault(u.DesmembramentoId!.Value)),
            Rateio = RateioEmpreendimento.Calcular(nos, custos),
        };
    }
}
