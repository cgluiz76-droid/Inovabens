using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Admin;

/* ------------------------------ Usuários ------------------------------ */

public sealed record UsuarioItem(Guid Id, string Nome, string Email, bool Ativo, bool TotpAtivo, DateTime? UltimoLoginEm, Guid PerfilId, string PerfilNome);

public sealed class UsuarioForm
{
    [Required(ErrorMessage = "Informe o nome."), StringLength(160, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 160 caracteres.")]
    public string Nome { get; set; } = "";

    [Required(ErrorMessage = "Informe o e-mail."), EmailAddress(ErrorMessage = "E-mail inválido."), StringLength(200)]
    public string Email { get; set; } = "";

    /// <summary>Obrigatória na criação; na edição, em branco mantém a atual.</summary>
    [SenhaForte] public string? Senha { get; set; }

    [Required(ErrorMessage = "Selecione o perfil.")] public Guid? PerfilId { get; set; }
    public bool Ativo { get; set; } = true;
}

/// <summary>Cadastro de usuários (port de users.module.ts).</summary>
public sealed class UsuariosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public async Task<Paginado<UsuarioItem>> ListarAsync(UsuarioAtual u, string? termo, int pagina = 1, int tamanho = 100)
    {
        u.Exigir(P.UsuariosGerir);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.Usuarios.AsNoTracking().Where(x => x.ExcluidoEm == null);
        if (Texto.Limpo(termo) is { } t)
        {
            var like = $"%{Texto.LikeLiteral(t)}%";
            q = q.Where(x => EF.Functions.ILike(x.Nome, like) || EF.Functions.ILike(x.Email, like));
        }
        pagina = Math.Max(1, pagina);
        var total = await q.CountAsync();
        var itens = await q.OrderBy(x => x.Nome).Skip((pagina - 1) * tamanho).Take(tamanho)
            .Select(x => new UsuarioItem(x.Id, x.Nome, x.Email, x.Ativo, x.TotpAtivo, x.UltimoLoginEm, x.PerfilId, x.Perfil!.Nome))
            .ToListAsync();
        return new Paginado<UsuarioItem>(itens, total, pagina, tamanho);
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, UsuarioForm form)
    {
        u.Exigir(P.UsuariosGerir);
        Validacao.Validar(form);
        if (string.IsNullOrEmpty(form.Senha)) throw new RegraNegocioException(nameof(UsuarioForm.Senha), "Informe a senha.");
        await using var db = await fabrica.CreateDbContextAsync();
        var novo = new Usuario
        {
            Id = Guid.NewGuid(), Nome = form.Nome.Trim(), Email = form.Email.Trim().ToLowerInvariant(),
            SenhaHash = Senhas.Hash(form.Senha), PerfilId = form.PerfilId!.Value, Ativo = form.Ativo,
        };
        db.Usuarios.Add(novo);
        audit.Registrar(db, u.Id, "usuarios", novo.Id, "CRIAR", depois: novo);
        await db.SaveChangesAsync();
        return novo.Id;
    }

    /// <summary>
    /// Troca de senha, desativação ou mudança de perfil derrubam as sessões do usuário na próxima revalidação
    /// (até 15 min). Ninguém desativa nem troca o perfil da própria conta.
    /// </summary>
    public async Task AtualizarAsync(UsuarioAtual u, Guid id, UsuarioForm form)
    {
        u.Exigir(P.UsuariosGerir);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var x = await db.Usuarios.SingleOrDefaultAsync(y => y.Id == id && y.ExcluidoEm == null) ?? throw new NaoEncontradoException("Usuário não encontrado.");
        if (id == u.Id && (!form.Ativo || form.PerfilId != x.PerfilId))
            throw new RegraNegocioException("_", "Você não pode desativar ou trocar o perfil da sua própria conta");
        var antes = new { x.Nome, x.Email, x.PerfilId, x.Ativo };
        x.Nome = form.Nome.Trim();
        x.Email = form.Email.Trim().ToLowerInvariant();
        x.PerfilId = form.PerfilId!.Value;
        x.Ativo = form.Ativo;
        var senhaAlterada = !string.IsNullOrEmpty(form.Senha);
        if (senhaAlterada)
        {
            x.SenhaHash = Senhas.Hash(form.Senha!);
            x.TentativasFalhas = 0;
            x.BloqueadoAte = null;
        }
        audit.Registrar(db, u.Id, "usuarios", id, "ATUALIZAR", antes, new { x.Nome, x.Email, x.PerfilId, x.Ativo, senha_alterada = senhaAlterada });
        await db.SaveChangesAsync();
    }

    public async Task DefinirAtivoAsync(UsuarioAtual u, Guid id, bool ativo)
    {
        u.Exigir(P.UsuariosGerir);
        await using var db = await fabrica.CreateDbContextAsync();
        var x = await db.Usuarios.SingleOrDefaultAsync(y => y.Id == id && y.ExcluidoEm == null) ?? throw new NaoEncontradoException("Usuário não encontrado.");
        if (id == u.Id && !ativo) throw new RegraNegocioException("_", "Você não pode desativar a sua própria conta");
        audit.Registrar(db, u.Id, "usuarios", id, "ATUALIZAR", new { x.Ativo }, new { Ativo = ativo });
        x.Ativo = ativo;
        await db.SaveChangesAsync();
    }
}

/* ------------------------- Perfis e permissões ------------------------- */

public sealed record PerfilItem(Guid Id, string Nome, string? Descricao, bool EscopoProprio, IReadOnlyList<string> Permissoes, int Usuarios);

public sealed class PerfilForm
{
    [Required(ErrorMessage = "Informe o nome."), StringLength(60, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 60 caracteres.")]
    public string Nome { get; set; } = "";
    [StringLength(300)] public string? Descricao { get; set; }
    public bool EscopoProprio { get; set; }
    public HashSet<string> Permissoes { get; set; } = [];
}

/// <summary>Perfis (RBAC) — port de rbac.module.ts. O perfil Administrador é protegido contra alteração e exclusão.</summary>
public sealed class PerfisService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public const string PerfilAdmin = "Administrador";

    public async Task<IReadOnlyList<PerfilItem>> ListarAsync(UsuarioAtual u)
    {
        if (!u.Pode(P.RbacGerir) && !u.Pode(P.UsuariosGerir)) throw new AcessoNegadoException();
        await using var db = await fabrica.CreateDbContextAsync();
        var perfis = await db.Perfis.AsNoTracking().OrderBy(p => p.Nome).ToListAsync();
        var links = await db.PerfilPermissoes.AsNoTracking().Select(pp => new { pp.PerfilId, pp.Permissao!.Codigo }).ToListAsync();
        var cont = await db.Usuarios.Where(x => x.ExcluidoEm == null).GroupBy(x => x.PerfilId).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        return [.. perfis.Select(p => new PerfilItem(p.Id, p.Nome, p.Descricao, p.EscopoProprio,
            [.. links.Where(l => l.PerfilId == p.Id).Select(l => l.Codigo).Order()], cont.FirstOrDefault(c => c.Key == p.Id)?.N ?? 0))];
    }

    private static async Task GravarPermissoes(InovabensDb db, Guid perfilId, IEnumerable<string> codigos)
    {
        await db.PerfilPermissoes.Where(pp => pp.PerfilId == perfilId).ExecuteDeleteAsync();
        var lista = codigos.ToList();
        var ids = await db.Permissoes.Where(p => lista.Contains(p.Codigo)).Select(p => p.Id).ToListAsync();
        db.PerfilPermissoes.AddRange(ids.Select(pid => new PerfilPermissao { PerfilId = perfilId, PermissaoId = pid }));
    }

    public async Task CriarAsync(UsuarioAtual u, PerfilForm form)
    {
        u.Exigir(P.RbacGerir);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var p = new Perfil { Id = Guid.NewGuid(), Nome = form.Nome.Trim(), Descricao = Texto.Limpo(form.Descricao), EscopoProprio = form.EscopoProprio };
        db.Perfis.Add(p);
        await db.SaveChangesAsync();
        await GravarPermissoes(db, p.Id, form.Permissoes.Intersect(P.Todas));
        audit.Registrar(db, u.Id, "perfis", p.Id, "CRIAR", depois: new { p.Nome, p.Descricao, p.EscopoProprio, permissoes = form.Permissoes.Order() });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, PerfilForm form)
    {
        u.Exigir(P.RbacGerir);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await db.Perfis.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Perfil não encontrado.");
        // trava de segurança: o Administrador não pode perder acesso (evita trancar todos fora do sistema)
        if (p.Nome == PerfilAdmin) throw new RegraNegocioException("_", "O perfil Administrador não pode ser alterado");
        var antesPerms = await db.PerfilPermissoes.Where(pp => pp.PerfilId == id).Select(pp => pp.Permissao!.Codigo).ToListAsync();
        var antes = new { p.Nome, p.Descricao, p.EscopoProprio, permissoes = antesPerms.Order() };
        await using var tx = await db.Database.BeginTransactionAsync();
        p.Nome = form.Nome.Trim();
        p.Descricao = Texto.Limpo(form.Descricao);
        p.EscopoProprio = form.EscopoProprio;
        await GravarPermissoes(db, id, form.Permissoes.Intersect(P.Todas));
        audit.Registrar(db, u.Id, "perfis", id, "ATUALIZAR", antes, new { p.Nome, p.Descricao, p.EscopoProprio, permissoes = form.Permissoes.Order() });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.RbacGerir);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await db.Perfis.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Perfil não encontrado.");
        if (p.Nome == PerfilAdmin) throw new RegraNegocioException("_", "O perfil Administrador não pode ser excluído");
        if (await db.Usuarios.AnyAsync(x => x.PerfilId == id))
            throw new RegraNegocioException("_", "Existem usuários neste perfil. Mova-os antes de excluir.");
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.PerfilPermissoes.Where(pp => pp.PerfilId == id).ExecuteDeleteAsync();
        db.Perfis.Remove(p);
        audit.Registrar(db, u.Id, "perfis", id, "EXCLUIR", new { p.Nome, p.Descricao, p.EscopoProprio });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}

/* ------------------------------ Auditoria ------------------------------ */

public sealed record AuditoriaItem(AuditLog Dados, string? UsuarioNome);

public sealed class AuditoriaService(IDbContextFactory<InovabensDb> fabrica)
{
    public async Task<Paginado<AuditoriaItem>> ListarAsync(UsuarioAtual u, string? entidade, Guid? entidadeId, int pagina = 1, int tamanho = 50)
    {
        u.Exigir(P.AuditoriaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.AuditLog.AsNoTracking();
        if (Texto.Limpo(entidade) is { } e) q = q.Where(a => a.Entidade == e);
        if (entidadeId is { } id) q = q.Where(a => a.EntidadeId == id);
        pagina = Math.Max(1, pagina);
        var total = await q.CountAsync();
        var itens = await (
            from a in q
            join us in db.Usuarios on a.UsuarioId equals us.Id into uu
            from us in uu.DefaultIfEmpty()
            orderby a.CriadoEm descending
            select new AuditoriaItem(a, us != null ? us.Nome : null)
        ).Skip((pagina - 1) * tamanho).Take(tamanho).ToListAsync();
        return new Paginado<AuditoriaItem>(itens, total, pagina, tamanho);
    }

    public async Task<IReadOnlyList<string>> EntidadesAsync(UsuarioAtual u)
    {
        u.Exigir(P.AuditoriaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.AuditLog.Select(a => a.Entidade).Distinct().OrderBy(x => x).ToListAsync();
    }
}
