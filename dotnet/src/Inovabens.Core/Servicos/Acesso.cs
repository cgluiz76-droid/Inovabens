using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Servicos;

/// <summary>
/// Escopo por imóvel (port de assertAcessoImovel), para TODA operação que acontece "dentro" de um imóvel:
/// perfil com escopo próprio (Corretor) só enxerga e altera os imóveis de que é o corretor responsável.
/// </summary>
public static class Acesso
{
    /// <summary>Filtra as consultas de imóveis pelo escopo do usuário.</summary>
    public static IQueryable<Imovel> VisiveisPara(this IQueryable<Imovel> q, UsuarioAtual u) =>
        u.EscopoProprio ? q.Where(i => i.CorretorResponsavelId == u.Id) : q;

    /// <summary>O imóvel (não excluído), ou NaoEncontrado / AcessoNegado.</summary>
    public static async Task<Imovel> ImovelComAcesso(InovabensDb db, UsuarioAtual u, Guid id, bool rastrear = true)
    {
        var q = rastrear ? db.Imoveis : db.Imoveis.AsNoTracking();
        var im = await q.SingleOrDefaultAsync(i => i.Id == id && i.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Imóvel não encontrado.");
        if (u.EscopoProprio && im.CorretorResponsavelId != u.Id) throw new AcessoNegadoException("Sem acesso a este imóvel.");
        return im;
    }
}
