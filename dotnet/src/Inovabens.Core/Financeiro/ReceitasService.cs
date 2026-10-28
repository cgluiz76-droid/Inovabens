using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>Receitas (venda/locação). O valor líquido é coluna gerada no banco. Port de receitas.service.ts.</summary>
public sealed class ReceitasService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public async Task<IReadOnlyList<Receita>> ListarAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await db.Receitas.AsNoTracking()
            .Where(r => r.ImovelId == imovelId && r.ExcluidoEm == null)
            .OrderBy(r => r.Data)
            .ToListAsync();
    }

    public async Task<Receita> CriarAsync(UsuarioAtual u, Guid imovelId, ReceitaForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var r = new Receita { ImovelId = imovelId };
        form.AplicarEm(r);
        db.Receitas.Add(r);
        audit.Registrar(db, u.Id, "receitas", r.Id, "CRIAR", depois: ComLiquido(r));
        await db.SaveChangesAsync(); // o EF lê de volta o valor_liquido calculado pelo banco
        return r;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, ReceitaForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await ReceitaComAcesso(db, u, id);
        var antes = r.Copia();
        form.AplicarEm(r);
        audit.Registrar(db, u.Id, "receitas", id, "ATUALIZAR", antes, ComLiquido(r));
        await db.SaveChangesAsync();
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var r = await ReceitaComAcesso(db, u, id);
        audit.Registrar(db, u.Id, "receitas", id, "EXCLUIR", antes: r.Copia());
        r.ExcluidoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task<Receita> ReceitaComAcesso(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var r = await db.Receitas.SingleOrDefaultAsync(x => x.Id == id && x.ExcluidoEm == null)
            ?? throw new NaoEncontradoException("Receita não encontrada.");
        await Acesso.ImovelComAcesso(db, u, r.ImovelId, rastrear: false);
        return r;
    }

    private static Receita ComLiquido(Receita r)
    {
        var c = r.Copia();
        c.ValorLiquido = c.ValorBruto - c.CustosVenda - c.Tributos;
        return c;
    }
}
