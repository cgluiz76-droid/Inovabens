using System.ComponentModel.DataAnnotations;
using ClosedXML.Excel;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Relatorios;
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

    /// <summary>
    /// Imóveis a que a pessoa está ligada (imovel_envolvidos). Null = não mexe nos vínculos (quem não usa o campo,
    /// como o CRM, não apaga o que já existe).
    /// </summary>
    public List<Guid>? Imoveis { get; set; }

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
        var q = Filtradas(db, f);
        var pagina = Math.Max(1, f.Pagina);
        var total = await q.CountAsync();
        var itens = await q.Include(p => p.Papeis).OrderBy(p => p.NomeRazao)
            .Skip((pagina - 1) * f.TamanhoPagina).Take(f.TamanhoPagina).ToListAsync();
        return new Paginado<Pessoa>(itens, total, pagina, f.TamanhoPagina);
    }

    /// <summary>Filtros da lista (busca e perfil), comuns à tela e à planilha.</summary>
    private static IQueryable<Pessoa> Filtradas(InovabensDb db, FiltroPessoas f)
    {
        var q = db.Pessoas.AsNoTracking().Where(p => p.ExcluidoEm == null);
        if (Texto.Limpo(f.Termo) is { } t)
        {
            var termo = $"%{Texto.LikeLiteral(t)}%";
            q = q.Where(p => EF.Functions.ILike(p.NomeRazao, termo) || EF.Functions.ILike(p.CpfCnpj!, termo)
                || EF.Functions.ILike(p.Email!, termo) || EF.Functions.ILike(p.Telefone!, termo));
        }
        if (Texto.Limpo(f.Papel) is { } papel) q = q.Where(p => p.Papeis.Any(x => x.Papel == papel));
        return q;
    }

    /// <summary>
    /// Planilha da lista de pessoas com os mesmos filtros e a mesma ordem da tela, todas as páginas
    /// (dados e contato, perfis e códigos dos imóveis ligados).
    /// </summary>
    public async Task<Arquivo> ExcelListaAsync(UsuarioAtual u, FiltroPessoas f)
    {
        u.Exigir(P.PessoasVer);
        u.Exigir(P.PessoasExportarLista);
        await using var db = await fabrica.CreateDbContextAsync();
        var pessoas = await Filtradas(db, f).Include(p => p.Papeis).OrderBy(p => p.NomeRazao).ToListAsync();
        var nomesPerfil = await db.PerfisPessoa.AsNoTracking().ToDictionaryAsync(p => p.Codigo, p => p.Nome);
        var codigos = await CodigosImoveisAsync(u, [.. pessoas.Select(p => p.Id)]);

        using var wb = new XLWorkbook();
        static XLCellValue T(string? s) => string.IsNullOrWhiteSpace(s) ? Blank.Value : s;
        var ws = RelatoriosService.Aba(wb, "Pessoas",
        [
            ("Nome / Razão social", 36), ("Tipo", 15), ("CPF/CNPJ", 20), ("Telefone", 16), ("WhatsApp", 16), ("E-mail", 30),
            ("Cidade", 20), ("UF", 5), ("Perfis", 24), ("Especialidade", 20), ("Imóveis", 30),
        ],
        pessoas.Select(p => new XLCellValue[]
        {
            p.NomeRazao, p.Tipo == TipoPessoa.PF ? "Pessoa física" : "Pessoa jurídica",
            T(string.IsNullOrEmpty(p.CpfCnpj) ? null : Formatos.Documento(p.CpfCnpj)),
            T(p.Telefone), T(p.Whatsapp), T(p.Email), T(p.Cidade), T(p.Uf),
            T(string.Join(", ", p.Papeis.Select(x => nomesPerfil.GetValueOrDefault(x.Papel) ?? Texto.Rotulo(x.Papel)))),
            T(string.Join(", ", p.Papeis.Select(x => x.Especialidade).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct())),
            T(codigos.TryGetValue(p.Id, out var cods) ? string.Join(", ", cods) : null),
        }));
        if (pessoas.Count > 0) ws.Range(1, 1, pessoas.Count + 1, 11).SetAutoFilter();

        audit.Registrar(db, u.Id, "pessoas", null, "EXPORTAR", depois: new
        {
            relatorio = "lista-pessoas", formato = "xlsx", linhas = pessoas.Count, filtros = new { f.Termo, f.Papel },
        });
        await db.SaveChangesAsync();
        return new Arquivo(RelatoriosService.Salvar(wb), $"pessoas-{DatasBR.Hoje():yyyy-MM-dd}.xlsx", RelatoriosService.TipoXlsx);
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
        await SincronizarImoveis(db, u, p, form);
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
        await SincronizarImoveis(db, u, p, form);
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

    /// <summary>Imóveis ligados à pessoa (para o campo de seleção do cadastro).</summary>
    public async Task<List<Guid>> ImoveisDaPessoaAsync(UsuarioAtual u, Guid pessoaId)
    {
        u.Exigir(P.PessoasVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.ImovelEnvolvidos.Where(e => e.PessoaId == pessoaId).Select(e => e.ImovelId).Distinct().ToListAsync();
    }

    /// <summary>Códigos dos imóveis de cada pessoa da lista (coluna "Imóveis").</summary>
    public async Task<Dictionary<Guid, List<string>>> CodigosImoveisAsync(UsuarioAtual u, IReadOnlyCollection<Guid> pessoas)
    {
        u.Exigir(P.PessoasVer);
        if (pessoas.Count == 0) return [];
        await using var db = await fabrica.CreateDbContextAsync();
        var pares = await db.ImovelEnvolvidos.Where(e => pessoas.Contains(e.PessoaId))
            .Join(db.Imoveis.Where(i => i.ExcluidoEm == null), e => e.ImovelId, i => i.Id, (e, i) => new { e.PessoaId, i.Codigo })
            .Distinct().ToListAsync();
        return pares.GroupBy(x => x.PessoaId).ToDictionary(g => g.Key, g => g.Select(x => x.Codigo).Order().ToList());
    }

    /// <summary>
    /// Grava os imóveis marcados: cria o vínculo (com o primeiro perfil da pessoa e a especialidade como observação) e
    /// remove os desmarcados. Imóveis que o usuário não enxerga (corretor) ficam como estão.
    /// </summary>
    private static async Task SincronizarImoveis(InovabensDb db, UsuarioAtual u, Pessoa p, PessoaForm form)
    {
        if (form.Imoveis is null) return;
        var marcados = form.Imoveis.Distinct().ToList();
        var visiveis = await db.Imoveis.Where(i => i.ExcluidoEm == null).VisiveisPara(u).Select(i => i.Id).ToListAsync();
        if (marcados.Except(visiveis).Any()) throw new RegraNegocioException("Imoveis", "Imóvel inválido.");

        var atuais = p.Id == Guid.Empty ? [] : await db.ImovelEnvolvidos.Where(e => e.PessoaId == p.Id).ToListAsync();
        db.ImovelEnvolvidos.RemoveRange(atuais.Where(e => visiveis.Contains(e.ImovelId) && !marcados.Contains(e.ImovelId)));
        var novos = marcados.Where(id => atuais.All(e => e.ImovelId != id)).ToList();
        if (novos.Count == 0) return;

        var codigos = p.Papeis.Select(x => x.Papel).ToList();
        var papel = await db.PerfisPessoa.Where(x => codigos.Contains(x.Codigo)).OrderBy(x => x.Ordem).Select(x => x.Codigo).FirstOrDefaultAsync()
            ?? throw new RegraNegocioException("Imoveis", "Marque um perfil da pessoa para ligá-la a imóveis.");
        var obs = p.Papeis.First(x => x.Papel == papel).Especialidade;
        db.ImovelEnvolvidos.AddRange(novos.Select(id => new ImovelEnvolvido { ImovelId = id, PessoaId = p.Id, Papel = papel, Observacao = obs }));
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
