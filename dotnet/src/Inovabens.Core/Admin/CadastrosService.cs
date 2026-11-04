using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Admin;

public sealed class TipoImovelForm
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(40, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 40 caracteres.")]
    public string Nome { get; set; } = "";

    public bool Ativo { get; set; } = true;
}

public sealed class PerfilPessoaForm
{
    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(60, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 60 caracteres.")]
    public string Nome { get; set; } = "";

    public bool Ativo { get; set; } = true;
}

/// <summary>
/// Cadastros auxiliares da Administração: tipos de imóvel e perfis de pessoa. Não há exclusão: um item fora de uso
/// é desativado (some das listas de seleção, mas continua nos registros que já o usam).
/// </summary>
public sealed class CadastrosService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    /* --------------------------- Tipos de imóvel --------------------------- */

    public async Task<IReadOnlyList<TipoImovel>> TiposImovelAsync(bool incluirInativos = false)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.TiposImovel.AsNoTracking().Where(t => incluirInativos || t.Ativo).OrderBy(t => t.Nome).ToListAsync();
    }

    public async Task<TipoImovel> CriarTipoImovelAsync(UsuarioAtual u, TipoImovelForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var nome = form.Nome.Trim();
        await ExigirNomeLivre(db.TiposImovel.Where(t => t.Nome.ToLower() == nome.ToLower()), "Já existe um tipo com esse nome.");
        var t = new TipoImovel { Nome = nome, Ativo = form.Ativo };
        db.TiposImovel.Add(t);
        audit.Registrar(db, u.Id, "tipos_imovel", t.Id, "CRIAR", depois: t);
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Renomear vale para os imóveis já cadastrados com o tipo (FK com ON UPDATE CASCADE).</summary>
    public async Task AtualizarTipoImovelAsync(UsuarioAtual u, Guid id, TipoImovelForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var t = await db.TiposImovel.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Tipo de imóvel não encontrado.");
        var nome = form.Nome.Trim();
        await ExigirNomeLivre(db.TiposImovel.Where(x => x.Id != id && x.Nome.ToLower() == nome.ToLower()), "Já existe um tipo com esse nome.");
        var antes = new TipoImovel { Id = t.Id, Nome = t.Nome, Ativo = t.Ativo };
        t.Nome = nome;
        t.Ativo = form.Ativo;
        audit.Registrar(db, u.Id, "tipos_imovel", id, "ATUALIZAR", antes, t);
        await db.SaveChangesAsync();
    }

    /* --------------------------- Perfis de pessoa --------------------------- */

    public async Task<IReadOnlyList<PerfilPessoa>> PerfisPessoaAsync(bool incluirInativos = false)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.PerfisPessoa.AsNoTracking().Where(p => incluirInativos || p.Ativo)
            .OrderBy(p => p.Ordem).ThenBy(p => p.Nome).ToListAsync();
    }

    public async Task<PerfilPessoa> CriarPerfilPessoaAsync(UsuarioAtual u, PerfilPessoaForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var nome = form.Nome.Trim();
        await ExigirNomeLivre(db.PerfisPessoa.Where(p => p.Nome.ToLower() == nome.ToLower()), "Já existe um perfil com esse nome.");

        var codigos = await db.PerfisPessoa.Select(p => p.Codigo).ToListAsync();
        var p = new PerfilPessoa
        {
            Codigo = CodigoLivre(nome, codigos), Nome = nome, Ativo = form.Ativo,
            Ordem = (await db.PerfisPessoa.MaxAsync(x => (int?)x.Ordem) ?? 0) + 1,
        };
        db.PerfisPessoa.Add(p);
        audit.Registrar(db, u.Id, "perfis_pessoa", null, "CRIAR", depois: p);
        await db.SaveChangesAsync();
        return p;
    }

    public async Task AtualizarPerfilPessoaAsync(UsuarioAtual u, string codigo, PerfilPessoaForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await db.PerfisPessoa.SingleOrDefaultAsync(x => x.Codigo == codigo) ?? throw new NaoEncontradoException("Perfil de pessoa não encontrado.");
        var nome = form.Nome.Trim();
        await ExigirNomeLivre(db.PerfisPessoa.Where(x => x.Codigo != codigo && x.Nome.ToLower() == nome.ToLower()), "Já existe um perfil com esse nome.");
        var antes = new PerfilPessoa { Codigo = p.Codigo, Nome = p.Nome, Ativo = p.Ativo, Ordem = p.Ordem };
        p.Nome = nome;
        p.Ativo = form.Ativo;
        audit.Registrar(db, u.Id, "perfis_pessoa", null, "ATUALIZAR", antes, p);
        await db.SaveChangesAsync();
    }

    /// <summary>"Corretor parceiro" → CORRETOR_PARCEIRO (com sufixo _2, _3... se já existir).</summary>
    public static string CodigoLivre(string nome, IReadOnlyCollection<string> existentes)
    {
        var sb = new StringBuilder();
        foreach (var c in nome.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }
        var baseCodigo = string.Join('_', sb.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));
        if (baseCodigo.Length == 0) baseCodigo = "PERFIL";
        if (baseCodigo.Length > 34) baseCodigo = baseCodigo[..34];
        var codigo = baseCodigo;
        for (var i = 2; existentes.Contains(codigo, StringComparer.OrdinalIgnoreCase); i++) codigo = $"{baseCodigo}_{i}";
        return codigo;
    }

    private static async Task ExigirNomeLivre<T>(IQueryable<T> mesmoNome, string mensagem)
    {
        if (await mesmoNome.AnyAsync()) throw new RegraNegocioException("Nome", mensagem);
    }
}
