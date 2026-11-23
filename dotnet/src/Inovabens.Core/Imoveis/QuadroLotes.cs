using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Imoveis;

/// <summary>Como o lote aparece para o público no quadro do empreendimento.</summary>
public enum SituacaoLoteSite { Disponivel, EmBreve, Vendido, Indisponivel }

/// <summary>Lote no quadro do site e da página do imóvel. Preço só quando está à venda.</summary>
/// <param name="TemPagina">O lote também é divulgado sozinho no site (tem ficha própria).</param>
public sealed record LoteSite(Guid Id, string Codigo, string Nome, int Fase, decimal? AreaM2, decimal? Preco, SituacaoLoteSite Situacao, bool TemPagina);

/// <summary>Quadro de lotes do empreendimento (sem as áreas remanescentes, que ainda não estão à venda).</summary>
public sealed record QuadroLotes(IReadOnlyList<LoteSite> Lotes)
{
    public int Disponiveis => Lotes.Count(l => l.Situacao == SituacaoLoteSite.Disponivel);
    public int Vendidos => Lotes.Count(l => l.Situacao == SituacaoLoteSite.Vendido);
    public int EmBreve => Lotes.Count(l => l.Situacao == SituacaoLoteSite.EmBreve);
    /// <summary>Preço "a partir de" do empreendimento: o menor entre os lotes disponíveis.</summary>
    public decimal? APartirDe => Lotes.Where(l => l.Situacao == SituacaoLoteSite.Disponivel && l.Preco > 0).Min(l => l.Preco);
}

/// <summary>
/// Dados públicos do empreendimento para o site, a página do imóvel e a vitrine. Lote sem matrícula própria (ou ainda
/// em regularização) aparece como "Em breve" e sem preço: vender ou prometer vender lote não registrado é proibido
/// (Lei 6.766/79, art. 37).
/// </summary>
public static class QuadroEmpreendimento
{
    private static bool AVenda(Imovel i) => i.Situacao == SituacaoImovel.DISPONIVEL && !string.IsNullOrWhiteSpace(i.Matricula);

    private static SituacaoLoteSite Situacao(Imovel i) => i.Situacao switch
    {
        SituacaoImovel.VENDIDO => SituacaoLoteSite.Vendido,
        SituacaoImovel.DISPONIVEL when AVenda(i) => SituacaoLoteSite.Disponivel,
        SituacaoImovel.DISPONIVEL or SituacaoImovel.EM_REGULARIZACAO => SituacaoLoteSite.EmBreve,
        _ => SituacaoLoteSite.Indisponivel,
    };

    /// <summary>Quadro do empreendimento <paramref name="raizId"/>; null se o imóvel não tem lotes.</summary>
    public static async Task<QuadroLotes?> CarregarAsync(InovabensDb db, Guid raizId, CancellationToken ct = default)
    {
        var lotes = await db.Imoveis.AsNoTracking()
            .Where(i => i.EmpreendimentoId == raizId && i.ExcluidoEm == null && i.UnidadeTipo != TipoUnidade.Remanescente)
            .ToListAsync(ct);
        if (lotes.Count == 0) return null;
        var fases = await db.Desmembramentos.AsNoTracking().Where(d => d.EmpreendimentoId == raizId)
            .ToDictionaryAsync(d => d.Id, d => d.Fase, ct);
        return new([.. lotes.OrderBy(i => i.UnidadeNumero).ThenBy(i => i.Codigo).Select(i =>
        {
            var s = Situacao(i);
            return new LoteSite(i.Id, i.Referencia ?? i.Codigo, i.UnidadeIdentificacao ?? i.Codigo,
                i.DesmembramentoId is { } d ? fases.GetValueOrDefault(d) : 0, i.AreaTerrenoM2,
                s == SituacaoLoteSite.Disponivel ? i.PrecoVenda : null, s, i.DivulgarSite && s == SituacaoLoteSite.Disponivel);
        })]);
    }

    /// <summary>"A partir de" de vários imóveis de uma vez (vitrine): só os que são empreendimento com lote à venda.</summary>
    public static async Task<Dictionary<Guid, decimal>> APartirDeAsync(InovabensDb db, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var linhas = await db.Imoveis.AsNoTracking()
            .Where(i => i.EmpreendimentoId != null && ids.Contains(i.EmpreendimentoId.Value) && i.ExcluidoEm == null
                && i.UnidadeTipo != TipoUnidade.Remanescente && i.Situacao == SituacaoImovel.DISPONIVEL
                && i.Matricula != null && i.Matricula != "" && i.PrecoVenda > 0)
            .GroupBy(i => i.EmpreendimentoId!.Value)
            .Select(g => new { Id = g.Key, Min = g.Min(i => i.PrecoVenda!.Value) })
            .ToListAsync(ct);
        return linhas.ToDictionary(x => x.Id, x => x.Min);
    }
}
