using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Financeiro;

/// <summary>Aquisição (CAPEX) e itens detalhados. Port de aquisicao.service.ts.</summary>
public sealed class AquisicaoService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    /// <summary>Aquisição + itens, ou null se ainda não cadastrada. O total vem do banco (coluna gerada).</summary>
    public async Task<AquisicaoDetalhe?> ObterAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.FinanceiroVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await ImoveisService.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var a = await db.Aquisicoes.AsNoTracking().SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        if (a is null) return null;
        var itens = await db.AquisicaoItens.AsNoTracking().Where(i => i.AquisicaoId == a.Id).OrderBy(i => i.CriadoEm).ToListAsync();
        return new AquisicaoDetalhe(a, itens);
    }

    public async Task SalvarAsync(UsuarioAtual u, Guid imovelId, AquisicaoForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await ImoveisService.ImovelComAcesso(db, u, imovelId, rastrear: false);

        var a = await db.Aquisicoes.SingleOrDefaultAsync(x => x.ImovelId == imovelId);
        if (a is null)
        {
            a = new Aquisicao { ImovelId = imovelId };
            form.AplicarEm(a, componentes: true);
            db.Aquisicoes.Add(a);
            audit.Registrar(db, u.Id, "aquisicoes", a.Id, "CRIAR", depois: ComTotal(a));
        }
        else
        {
            var antes = a.Copia();
            // Se já existem itens detalhados, eles mandam em impostos/taxas/despesas (trigger); só os demais campos mudam.
            var temItens = await db.AquisicaoItens.AnyAsync(i => i.AquisicaoId == a.Id);
            form.AplicarEm(a, componentes: !temItens);
            audit.Registrar(db, u.Id, "aquisicoes", a.Id, "ATUALIZAR", antes, ComTotal(a));
        }
        await db.SaveChangesAsync();
    }

    public async Task CriarItemAsync(UsuarioAtual u, Guid imovelId, AquisicaoItemForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await ImoveisService.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var aquisicaoId = await db.Aquisicoes.Where(x => x.ImovelId == imovelId).Select(x => (Guid?)x.Id).SingleOrDefaultAsync()
            ?? throw new RegraNegocioException("itens", "Cadastre a aquisição (data e valor pago) antes de lançar itens.");
        var item = new AquisicaoItem { AquisicaoId = aquisicaoId };
        form.AplicarEm(item);
        db.AquisicaoItens.Add(item);
        audit.Registrar(db, u.Id, "aquisicao_itens", item.Id, "CRIAR", depois: item);
        await db.SaveChangesAsync();
    }

    public async Task AtualizarItemAsync(UsuarioAtual u, Guid itemId, AquisicaoItemForm form)
    {
        u.Exigir(P.FinanceiroEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await ItemComAcesso(db, u, itemId);
        var antes = item.Copia();
        form.AplicarEm(item);
        audit.Registrar(db, u.Id, "aquisicao_itens", itemId, "ATUALIZAR", antes, item);
        await db.SaveChangesAsync();
    }

    public async Task ExcluirItemAsync(UsuarioAtual u, Guid itemId)
    {
        u.Exigir(P.FinanceiroEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var item = await ItemComAcesso(db, u, itemId);
        audit.Registrar(db, u.Id, "aquisicao_itens", itemId, "EXCLUIR", antes: item.Copia());
        db.AquisicaoItens.Remove(item);
        await db.SaveChangesAsync();
    }

    private static async Task<AquisicaoItem> ItemComAcesso(InovabensDb db, UsuarioAtual u, Guid itemId)
    {
        var item = await db.AquisicaoItens.Include(i => i.Aquisicao).SingleOrDefaultAsync(i => i.Id == itemId)
            ?? throw new NaoEncontradoException("Item não encontrado.");
        await ImoveisService.ImovelComAcesso(db, u, item.Aquisicao!.ImovelId, rastrear: false);
        return item;
    }

    /// <summary>Para a auditoria: o total é coluna gerada e só existe no banco depois de gravar.</summary>
    private static Aquisicao ComTotal(Aquisicao a)
    {
        var c = a.Copia();
        c.TotalAquisicao = c.ValorPago + c.Impostos + c.Taxas + c.DespesasDiversas;
        return c;
    }
}
