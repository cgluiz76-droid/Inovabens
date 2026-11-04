using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Pessoas;

public sealed record FiltroPessoas(string? Termo = null, string? Papel = null, int Pagina = 1, int TamanhoPagina = 20);

public sealed class PapelEntrada
{
    /// <summary>Código do perfil de pessoa (cadastro da Administração).</summary>
    public string Papel { get; set; } = "";
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
    [StringLength(200)] public string? Email { get; set; }
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

    /// <summary>CPF/CNPJ sem máscara (como grava a versão Node); o CNPJ alfanumérico mantém as letras.</summary>
    public string? DocumentoDigitos => Formatos.DocumentoSemMascara(CpfCnpj);

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (DocumentoDigitos is { } d)
        {
            if (Tipo == TipoPessoa.PF && !Formatos.CpfValido(d))
                yield return new ValidationResult("CPF deve ter 11 dígitos.", [nameof(CpfCnpj)]);
            if (Tipo == TipoPessoa.PJ && !Formatos.CnpjValido(d))
                yield return new ValidationResult("CNPJ deve ter 14 caracteres (12 letras ou dígitos + 2 dígitos).", [nameof(CpfCnpj)]);
        }
        if (Texto.Limpo(Email) is { } e && !Formatos.Email().IsMatch(e))
            yield return new ValidationResult("E-mail inválido.", [nameof(Email)]);
        if (Formatos.TelefoneDigitos(Telefone) is { } t && !Formatos.TelefoneValido(t))
            yield return new ValidationResult("Telefone com DDD: (11) 3456-7890 ou (11) 98765-4321.", [nameof(Telefone)]);
        if (Formatos.TelefoneDigitos(Whatsapp) is { } w && !Formatos.TelefoneValido(w))
            yield return new ValidationResult("WhatsApp com DDD: (11) 98765-4321.", [nameof(Whatsapp)]);
        if (Texto.Limpo(Uf) is not null && !Formatos.UfValida(Uf))
            yield return new ValidationResult("Selecione uma UF válida.", [nameof(Uf)]);
    }

    public static PessoaForm De(Pessoa p) => new()
    {
        Tipo = p.Tipo, NomeRazao = p.NomeRazao, CpfCnpj = p.CpfCnpj is null ? null : Formatos.Documento(p.CpfCnpj), Rg = p.Rg,
        Email = p.Email, Telefone = Formatos.TelefoneFormatado(p.Telefone), Whatsapp = Formatos.TelefoneFormatado(p.Whatsapp), Cep = p.Cep, Logradouro = p.Logradouro, Numero = p.Numero, Complemento = p.Complemento,
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
        p.Telefone = Formatos.TelefoneFormatado(Telefone);
        p.Whatsapp = Formatos.TelefoneFormatado(Whatsapp);
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
        if (Texto.Limpo(f.Papel) is { } papel) q = q.Where(p => p.Papeis.Any(x => x.Papel == papel));
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
        await ExigirPerfisValidos(db, form);
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
        await ExigirPerfisValidos(db, form);
        var antes = p.Copia();
        form.AplicarEm(p);
        SincronizarPapeis(db, p, form.PapeisPara(p.Id));
        audit.Registrar(db, u.Id, "pessoas", id, "ATUALIZAR", antes, p);
        await db.SaveChangesAsync();
    }

    private static async Task ExigirPerfisValidos(InovabensDb db, PessoaForm form)
    {
        var codigos = form.Papeis.Select(x => x.Papel).Distinct().ToList();
        if (codigos.Count == 0) return;
        var existentes = await db.PerfisPessoa.CountAsync(x => codigos.Contains(x.Codigo));
        if (existentes != codigos.Count) throw new RegraNegocioException("Papeis", "Perfil de pessoa inválido.");
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
