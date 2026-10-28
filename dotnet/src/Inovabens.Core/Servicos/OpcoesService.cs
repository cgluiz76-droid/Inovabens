using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Microsoft.EntityFrameworkCore;

namespace Inovabens.Core.Servicos;

/// <summary>Listas para caixas de seleção (corretores, pessoas).</summary>
public sealed class OpcoesService(IDbContextFactory<InovabensDb> fabrica)
{
    public async Task<IReadOnlyList<Opcao>> CorretoresAsync()
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Usuarios.AsNoTracking()
            .Where(u => u.Ativo && u.ExcluidoEm == null)
            .OrderBy(u => u.Nome)
            .Select(u => new Opcao(u.Id, u.Nome))
            .ToListAsync();
    }

    /// <summary>Pessoas cadastradas (exige pessoas:ver; sem a permissão a lista vem vazia, como na versão Node).</summary>
    public async Task<IReadOnlyList<Opcao>> PessoasAsync(UsuarioAtual u, int limite = 500)
    {
        if (!u.Pode(Dominio.Permissoes.PessoasVer)) return [];
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Pessoas.AsNoTracking()
            .Where(p => p.ExcluidoEm == null)
            .OrderBy(p => p.NomeRazao)
            .Take(limite)
            .Select(p => new Opcao(p.Id, p.NomeRazao))
            .ToListAsync();
    }

    /// <summary>Imóveis visíveis ao usuário (corretor: só os seus), rotulados "código · título".</summary>
    public async Task<IReadOnlyList<Opcao>> ImoveisAsync(UsuarioAtual u)
    {
        if (!u.Pode(Dominio.Permissoes.ImoveisVer)) return [];
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.Imoveis.AsNoTracking().Where(i => i.ExcluidoEm == null);
        if (u.EscopoProprio) q = q.Where(i => i.CorretorResponsavelId == u.Id);
        return await q.OrderBy(i => i.Codigo)
            .Select(i => new Opcao(i.Id, i.Codigo + " · " + i.Titulo))
            .ToListAsync();
    }
}
