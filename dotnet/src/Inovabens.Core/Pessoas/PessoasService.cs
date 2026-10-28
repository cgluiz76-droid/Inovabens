using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Pessoas;

public sealed record FiltroPessoas(string? Termo = null, PapelPessoa? Papel = null, int Pagina = 1, int TamanhoPagina = 20);

public sealed class PapelEntrada
{
    public PapelPessoa Papel { get; set; }
    [StringLength(100)] public string? Especialidade { get; set; }
}

public sealed class PessoaForm : IValidatableObject
{
    public TipoPessoa Tipo { get; set; } = TipoPessoa.PF;

    [Required(ErrorMessage = "Informe o nome.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 200 caracteres.")]
    public string NomeRazao { get; set; } = "";

    public string? CpfCnpj { get; set; }
    [StringLength(30)] public string? Rg { get; set; }
    [EmailAddress(ErrorMessage = "E-mail inválido."), StringLength(200)] public string? Email { get; set; }
    [StringLength(30)] public string? Telefone { get; set; }
    [StringLength(30)] public string? Whatsapp { get; set; }
    [StringLength(12)] public string? Cep { get; set; }
    [StringLength(200)] public string? Logradouro { get; set; }
    [StringLength(20)] public string? Numero { get; set; }
    [StringLength(100)] public string? Complemento { get; set; }
    [StringLength(100)] public string? Bairro { get; set; }
    [StringLength(100)] public string? Cidade { get; set; }
    public string? Uf { get; set; }
    public string? Observacoes { get; set; }
    public DateTime? LgpdConsentimentoEm { get; set; }
    public List<PapelEntrada> Papeis { get; set; } = [];

    /// <summary>Só os dígitos do CPF/CNPJ (como grava a versão Node).</summary>
    public string? DocumentoDigitos => Texto.Limpo(CpfCnpj) is { } d ? new string(d.Where(char.IsDigit).ToArray()) : null;

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (DocumentoDigitos is { } d && d.Length is not (11 or 14))
            yield return new ValidationResult("CPF (11 dígitos) ou CNPJ (14 dígitos)", [nameof(CpfCnpj)]);
        if (Texto.Limpo(Uf) is { Length: not 2 })
            yield return new ValidationResult("UF com 2 letras", [nameof(Uf)]);
    }

    public static PessoaForm De(Pessoa p) => new()
    {
        Tipo = p.Tipo, NomeRazao = p.NomeRazao, CpfCnpj = p.CpfCnpj, Rg = p.Rg, Email = p.Email, Telefone = p.Telefone,
        Whatsapp = p.Whatsapp, Cep = p.Cep, Logradouro = p.Logradouro, Numero = p.Numero, Complemento = p.Complemento,
        Bairro = p.Bairro, Cidade = p.Cidade, Uf = p.Uf, Observacoes = p.Observacoes, LgpdConsentimentoEm = p.LgpdConsentimentoEm,
        Papeis = [.. p.Papeis.Select(x => new PapelEntrada { Papel = x.Papel, Especialidade = x.Especialidade })],
    };

    internal void AplicarEm(Pessoa p)
    {
        p.Tipo = Tipo;
        p.NomeRazao = NomeRazao.Trim();
        p.CpfCnpj = DocumentoDigitos;
        p.Rg = Texto.Limpo(Rg);
        p.Email = Texto.Limpo(Email)?.ToLowerInvariant();
        p.Telefone = Texto.Limpo(Telefone);
        p.Whatsapp = Texto.Limpo(Whatsapp);
        p.Cep = Texto.Limpo(Cep);
        p.Logradouro = Texto.Limpo(Logradouro);
        p.Numero = Texto.Limpo(Numero);
        p.Complemento = Texto.Limpo(Complemento);
        p.Bairro = Texto.Limpo(Bairro);
        p.Cidade = Texto.Limpo(Cidade);
        p.Uf = Texto.Limpo(Uf)?.ToUpperInvariant();
        p.Observacoes = Texto.Limpo(Observacoes);
        p.LgpdConsentimentoEm = LgpdConsentimentoEm is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null;
    }

    internal List<PessoaPapel> PapeisPara(Guid pessoaId) =>
        [.. Papeis.GroupBy(x => x.Papel).Select(g => new PessoaPapel { PessoaId = pessoaId, Papel = g.Key, Especialidade = Texto.Limpo(g.First().Especialidade) })];
}

/// <summary>Port de pessoas.module.ts: cadastro único de clientes, proprietários, fiadores, parceiros, prestadores e terceiros.</summary>
public sealed class PessoasService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public async Task<Paginado<Pessoa>> ListarAsync(UsuarioAtual u, FiltroPessoas f)
    {
        u.Exigir(P.PessoasVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.Pessoas.AsNoTracking().Where(p => p.ExcluidoEm == null);
        if (Texto.Limpo(f.Termo) is { } t)
        {
            var termo = $"%{Texto.LikeLiteral(t)}%";
            q = q.Where(p => EF.Functions.ILike(p.NomeRazao, termo) || EF.Functions.ILike(p.CpfCnpj!, termo)
                || EF.Functions.ILike(p.Email!, termo) || EF.Functions.ILike(p.Telefone!, termo));
        }
        if (f.Papel is { } papel) q = q.Where(p => p.Papeis.Any(x => x.Papel == papel));
        var pagina = Math.Max(1, f.Pagina);
        var total = await q.CountAsync();
        var itens = await q.Include(p => p.Papeis).OrderBy(p => p.NomeRazao)
            .Skip((pagina - 1) * f.TamanhoPagina).Take(f.TamanhoPagina).ToListAsync();
        return new Paginado<Pessoa>(itens, total, pagina, f.TamanhoPagina);
    }

    public async Task<Pessoa> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.PessoasVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await Carregar(db, id, rastrear: false);
    }

    private static async Task<Pessoa> Carregar(InovabensDb db, Guid id, bool rastrear)
    {
        var q = db.Pessoas.Include(p => p.Papeis).Where(p => p.Id == id && p.ExcluidoEm == null);
        return await (rastrear ? q : q.AsNoTracking()).SingleOrDefaultAsync() ?? throw new NaoEncontradoException("Pessoa não encontrada.");
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, PessoaForm form)
    {
        u.Exigir(P.PessoasCriar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = new Pessoa { Id = Guid.NewGuid(), CriadoPor = u.Id };
        form.AplicarEm(p);
        p.Papeis = form.PapeisPara(p.Id);
        db.Pessoas.Add(p);
        audit.Registrar(db, u.Id, "pessoas", p.Id, "CRIAR", depois: p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, PessoaForm form)
    {
        u.Exigir(P.PessoasEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Carregar(db, id, rastrear: true);
        var antes = p.Copia();
        form.AplicarEm(p);
        SincronizarPapeis(db, p, form.PapeisPara(p.Id));
        audit.Registrar(db, u.Id, "pessoas", id, "ATUALIZAR", antes, p);
        await db.SaveChangesAsync();
    }

    /// <summary>Atualiza a lista de papéis no lugar (chave composta pessoa+papel não pode ser apagada e recriada no mesmo SaveChanges).</summary>
    private static void SincronizarPapeis(InovabensDb db, Pessoa p, List<PessoaPapel> novos)
    {
        foreach (var atual in p.Papeis.ToList())
        {
            var igual = novos.FirstOrDefault(n => n.Papel == atual.Papel);
            if (igual is null) { db.PessoaPapeis.Remove(atual); p.Papeis.Remove(atual); }
            else atual.Especialidade = igual.Especialidade;
        }
        foreach (var n in novos.Where(n => p.Papeis.All(a => a.Papel != n.Papel))) p.Papeis.Add(n);
    }

    /// <summary>Exclusão lógica; o CPF/CNPJ é liberado para um novo cadastro (índice único parcial).</summary>
    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.PessoasExcluir);
        await using var db = await fabrica.CreateDbContextAsync();
        var p = await Carregar(db, id, rastrear: true);
        var antes = p.Copia();
        p.ExcluidoEm = DateTime.UtcNow;
        p.CpfCnpj = null;
        audit.Registrar(db, u.Id, "pessoas", id, "EXCLUIR", antes);
        await db.SaveChangesAsync();
    }
}
