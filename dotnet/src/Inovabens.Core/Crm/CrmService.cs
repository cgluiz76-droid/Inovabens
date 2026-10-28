using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

public sealed record LeadCartao(
    Guid Id, string PessoaNome, string? Telefone, string? Email, FinalidadeLead Finalidade, string? Origem,
    string? ResponsavelNome, decimal? OrcamentoMax, DateTime? ProximaAcaoEm, int Ordem);

public sealed record ColunaFunil(FunilEstagio Estagio, IReadOnlyList<LeadCartao> Leads);

public sealed record LeadDetalhe(Lead Dados, Pessoa Pessoa, FunilEstagio Estagio, string? ResponsavelNome, IReadOnlyList<ImovelInteresse> ImoveisInteresse);

public sealed record ImovelInteresse(Guid Id, string Codigo, string Titulo, SituacaoImovel Situacao);

public sealed record HistoricoEstagio(Guid Id, DateTime CriadoEm, string? EstagioAnterior, string EstagioNovo, string? Usuario);

public sealed record InteracaoDetalhe(LeadInteracao Dados, string? UsuarioNome);

public sealed class LeadForm : IValidatableObject
{
    public FinalidadeLead Finalidade { get; set; } = FinalidadeLead.COMPRA;
    [StringLength(80)] public string? Origem { get; set; }
    public Guid? ResponsavelId { get; set; }
    [Range(0, 9_999_999_999_999.99, ErrorMessage = "O valor não pode ser negativo")] public decimal? OrcamentoMin { get; set; }
    [Range(0, 9_999_999_999_999.99, ErrorMessage = "O valor não pode ser negativo")] public decimal? OrcamentoMax { get; set; }
    public DateTime? ProximaAcaoEm { get; set; }

    /// <summary>Pessoa existente OU dados de um novo contato (só na criação).</summary>
    public Guid? PessoaId { get; set; }
    [StringLength(200)] public string? NovoNome { get; set; }
    [StringLength(30)] public string? NovoTelefone { get; set; }
    [EmailAddress(ErrorMessage = "E-mail inválido.")] public string? NovoEmail { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (OrcamentoMin is { } a && OrcamentoMax is { } b && b < a)
            yield return new ValidationResult("O orçamento máximo deve ser maior que o mínimo", [nameof(OrcamentoMax)]);
    }

    internal void AplicarEm(Lead l)
    {
        l.Origem = Texto.Limpo(Origem);
        l.OrcamentoMin = OrcamentoMin;
        l.OrcamentoMax = OrcamentoMax;
        l.ProximaAcaoEm = ProximaAcaoEm is { } p ? DatasBR.ParaUtc(p) : null;
    }
}

public sealed class InteracaoForm
{
    public CanalInteracao Canal { get; set; } = CanalInteracao.WHATSAPP;
    [Required(ErrorMessage = "Descreva o contato."), MinLength(2, ErrorMessage = "Descreva o contato.")]
    public string Resumo { get; set; } = "";
}

public sealed class EstagioForm
{
    public Funil Funil { get; set; } = Funil.VENDA;
    [Required(ErrorMessage = "Informe o nome."), StringLength(60, MinimumLength = 2, ErrorMessage = "O nome deve ter de 2 a 60 caracteres.")]
    public string Nome { get; set; } = "";
    [Range(0, int.MaxValue)] public int Ordem { get; set; }
    [RegularExpression("^#[0-9a-fA-F]{6}$", ErrorMessage = "Cor no formato #RRGGBB")] public string Cor { get; set; } = "#64748b";
    public TipoEstagio Tipo { get; set; } = TipoEstagio.ABERTO;
    public bool Ativo { get; set; } = true;
}

/// <summary>CRM: funil de vendas/locação (port de crm.service.ts). Corretor só vê os leads pelos quais é responsável.</summary>
public sealed class CrmService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public static Funil FunilDa(FinalidadeLead f) => f == FinalidadeLead.COMPRA ? Funil.VENDA : Funil.LOCACAO;
    private static FinalidadeLead FinalidadeDo(Funil f) => f == Funil.VENDA ? FinalidadeLead.COMPRA : FinalidadeLead.LOCACAO;

    private static IQueryable<Lead> Visiveis(InovabensDb db, UsuarioAtual u)
    {
        var q = db.Leads.Where(l => l.ExcluidoEm == null);
        return u.EscopoProprio ? q.Where(l => l.ResponsavelId == u.Id) : q;
    }

    /* ----------------------------- Pipeline ----------------------------- */

    public async Task<IReadOnlyList<ColunaFunil>> PipelineAsync(UsuarioAtual u, Funil funil)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var estagios = await db.FunilEstagios.AsNoTracking().Where(e => e.Funil == funil && e.Ativo).OrderBy(e => e.Ordem).ToListAsync();
        var finalidade = FinalidadeDo(funil);
        var leads = await Visiveis(db, u).AsNoTracking().Where(l => l.Finalidade == finalidade)
            .OrderBy(l => l.Ordem).ThenByDescending(l => l.AtualizadoEm)
            .Select(l => new
            {
                l.EstagioId,
                Cartao = new LeadCartao(l.Id, l.Pessoa!.NomeRazao, l.Pessoa.Telefone, l.Pessoa.Email, l.Finalidade, l.Origem,
                    l.Responsavel != null ? l.Responsavel.Nome : null, l.OrcamentoMax, l.ProximaAcaoEm, l.Ordem),
            })
            .ToListAsync();
        return [.. estagios.Select(e => new ColunaFunil(e, [.. leads.Where(l => l.EstagioId == e.Id).Select(l => l.Cartao)]))];
    }

    /* ------------------------------ Leads ------------------------------ */

    public async Task<LeadDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return await Detalhe(db, u, id);
    }

    private static async Task<LeadDetalhe> Detalhe(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var l = await Visiveis(db, u).AsNoTracking().Include(x => x.Pessoa).Include(x => x.Estagio).Include(x => x.Responsavel)
            .SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Lead não encontrado.");
        var interesse = await db.LeadImoveisInteresse.AsNoTracking().Where(i => i.LeadId == id)
            .Join(db.Imoveis, i => i.ImovelId, im => im.Id, (i, im) => new ImovelInteresse(im.Id, im.Codigo, im.Titulo, im.Situacao))
            .ToListAsync();
        return new LeadDetalhe(l, l.Pessoa!, l.Estagio!, l.Responsavel?.Nome, interesse);
    }

    private static async Task<FunilEstagio> EstagioInicial(InovabensDb db, Funil funil) =>
        await db.FunilEstagios.Where(e => e.Funil == funil && e.Ativo && e.Tipo == TipoEstagio.ABERTO).OrderBy(e => e.Ordem).FirstOrDefaultAsync()
        ?? throw new RegraNegocioException("EstagioId", $"O funil {funil} não tem estágios ativos");

    private static async Task<FunilEstagio> EstagioDoFunil(InovabensDb db, Guid estagioId, Funil funil)
    {
        var e = await db.FunilEstagios.SingleOrDefaultAsync(x => x.Id == estagioId);
        if (e is null || !e.Ativo) throw new RegraNegocioException("EstagioId", "Estágio inexistente ou inativo");
        if (e.Funil != funil) throw new RegraNegocioException("EstagioId", $"Este estágio pertence ao funil {e.Funil}");
        return e;
    }

    public async Task<Guid> CriarAsync(UsuarioAtual u, LeadForm form)
    {
        u.Exigir(P.CrmCriar);
        Validacao.Validar(form);
        if (form.PessoaId is null && (Texto.Limpo(form.NovoNome)?.Length ?? 0) < 2)
            throw new RegraNegocioException(nameof(LeadForm.NovoNome), "Informe a pessoa ou o nome do novo contato");
        await using var db = await fabrica.CreateDbContextAsync();
        var estagio = await EstagioInicial(db, FunilDa(form.Finalidade));
        await using var tx = await db.Database.BeginTransactionAsync();

        Guid pessoaId;
        if (form.PessoaId is { } pid)
        {
            if (!await db.Pessoas.AnyAsync(p => p.Id == pid && p.ExcluidoEm == null)) throw new NaoEncontradoException("Pessoa não encontrada.");
            pessoaId = pid;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"insert into pessoa_papeis (pessoa_id, papel) values ({pid}, 'CLIENTE'::papel_pessoa) on conflict do nothing");
        }
        else
        {
            var p = new Pessoa
            {
                Id = Guid.NewGuid(), NomeRazao = form.NovoNome!.Trim(), Telefone = Texto.Limpo(form.NovoTelefone),
                Email = Texto.Limpo(form.NovoEmail)?.ToLowerInvariant(), CriadoPor = u.Id,
            };
            p.Papeis.Add(new PessoaPapel { PessoaId = p.Id, Papel = PapelPessoa.CLIENTE });
            db.Pessoas.Add(p);
            pessoaId = p.Id;
        }

        var l = new Lead
        {
            Id = Guid.NewGuid(), PessoaId = pessoaId, Finalidade = form.Finalidade, EstagioId = estagio.Id,
            ResponsavelId = u.EscopoProprio ? u.Id : form.ResponsavelId ?? u.Id,
        };
        form.AplicarEm(l);
        db.Leads.Add(l);
        db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = l.Id, EstagioNovoId = estagio.Id, UsuarioId = u.Id });
        audit.Registrar(db, u.Id, "leads", l.Id, "CRIAR", depois: l);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return l.Id;
    }

    /// <summary>Troca de estágio só por <see cref="MoverAsync"/> (gera histórico).</summary>
    public async Task AtualizarAsync(UsuarioAtual u, Guid id, LeadForm form)
    {
        u.Exigir(P.CrmEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Lead não encontrado.");
        var antes = l.Copia();
        form.AplicarEm(l);
        if (form.ResponsavelId is { } r && !u.EscopoProprio) l.ResponsavelId = r;
        audit.Registrar(db, u.Id, "leads", id, "ATUALIZAR", antes, l);
        await db.SaveChangesAsync();
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmExcluir);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Lead não encontrado.");
        var antes = l.Copia();
        l.ExcluidoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "leads", id, "EXCLUIR", antes);
        await db.SaveChangesAsync();
    }

    /// <summary>Move o lead no funil, registra o histórico e exige motivo ao marcar como Perdido.</summary>
    public async Task MoverAsync(UsuarioAtual u, Guid id, Guid estagioId, string? motivoPerda = null, int? ordem = null)
    {
        u.Exigir(P.CrmEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).Include(x => x.Estagio).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new NaoEncontradoException("Lead não encontrado.");
        var novo = await EstagioDoFunil(db, estagioId, FunilDa(l.Finalidade));
        var motivo = Texto.Limpo(motivoPerda);
        if (novo.Tipo == TipoEstagio.PERDIDO && motivo is null) throw new RegraNegocioException("MotivoPerda", "Informe o motivo da perda");
        if (motivo is { Length: > 500 }) throw new RegraNegocioException("MotivoPerda", "No máximo 500 caracteres");

        var anterior = l.Estagio!;
        await using var tx = await db.Database.BeginTransactionAsync();
        var prox = (await db.Leads.Where(x => x.EstagioId == novo.Id).MaxAsync(x => (int?)x.Ordem) ?? -1) + 1;
        l.EstagioId = novo.Id;
        l.Estagio = novo;
        l.Ordem = ordem ?? prox;
        l.MotivoPerda = novo.Tipo == TipoEstagio.PERDIDO ? motivo : null;
        if (novo.Id != anterior.Id)
            db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = id, EstagioAnteriorId = anterior.Id, EstagioNovoId = novo.Id, UsuarioId = u.Id });
        audit.Registrar(db, u.Id, "leads", id, "ATUALIZAR", new { estagio = anterior.Nome }, new { estagio = novo.Nome, motivo_perda = motivo });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<HistoricoEstagio>> HistoricoAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Detalhe(db, u, id);
        return await (
            from h in db.LeadHistoricoEstagios.AsNoTracking()
            where h.LeadId == id
            join a in db.FunilEstagios on h.EstagioAnteriorId equals a.Id into aa
            from a in aa.DefaultIfEmpty()
            join n in db.FunilEstagios on h.EstagioNovoId equals n.Id
            join us in db.Usuarios on h.UsuarioId equals us.Id into uu
            from us in uu.DefaultIfEmpty()
            orderby h.CriadoEm descending
            select new HistoricoEstagio(h.Id, h.CriadoEm, a != null ? a.Nome : null, n.Nome, us != null ? us.Nome : null)
        ).ToListAsync();
    }

    public async Task<IReadOnlyList<InteracaoDetalhe>> InteracoesAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Detalhe(db, u, id);
        return await (
            from i in db.LeadInteracoes.AsNoTracking()
            where i.LeadId == id
            join us in db.Usuarios on i.UsuarioId equals us.Id into uu
            from us in uu.DefaultIfEmpty()
            orderby i.CriadoEm descending
            select new InteracaoDetalhe(i, us != null ? us.Nome : null)
        ).ToListAsync();
    }

    public async Task CriarInteracaoAsync(UsuarioAtual u, Guid id, InteracaoForm form)
    {
        u.Exigir(P.CrmEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Lead não encontrado.");
        db.LeadInteracoes.Add(new LeadInteracao { LeadId = id, UsuarioId = u.Id, Canal = form.Canal, Resumo = form.Resumo.Trim() });
        l.AtualizadoEm = DateTime.UtcNow;
        db.Entry(l).State = EntityState.Modified;
        await db.SaveChangesAsync();
    }

    public async Task DefinirInteresseAsync(UsuarioAtual u, Guid id, IReadOnlyCollection<Guid> imoveis)
    {
        u.Exigir(P.CrmEditar);
        if (imoveis.Count > 100) throw new RegraNegocioException("_", "No máximo 100 imóveis de interesse");
        await using var db = await fabrica.CreateDbContextAsync();
        await Detalhe(db, u, id);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.LeadImoveisInteresse.Where(i => i.LeadId == id).ExecuteDeleteAsync();
        db.LeadImoveisInteresse.AddRange(imoveis.Distinct().Select(im => new LeadImovelInteresse { LeadId = id, ImovelId = im }));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    /* ----------------------- Estágios (configuráveis) ----------------------- */

    public async Task<IReadOnlyList<FunilEstagio>> EstagiosAsync(Funil? funil = null)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.FunilEstagios.AsNoTracking();
        if (funil is { } f) q = q.Where(e => e.Funil == f);
        return await q.OrderBy(e => e.Funil).ThenBy(e => e.Ordem).ToListAsync();
    }

    public async Task CriarEstagioAsync(UsuarioAtual u, EstagioForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        db.FunilEstagios.Add(new FunilEstagio { Funil = form.Funil, Nome = form.Nome.Trim(), Ordem = form.Ordem, Cor = form.Cor, Tipo = form.Tipo, Ativo = form.Ativo });
        await db.SaveChangesAsync();
    }

    public async Task AtualizarEstagioAsync(UsuarioAtual u, Guid id, EstagioForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var e = await db.FunilEstagios.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Estágio não encontrado.");
        (e.Nome, e.Ordem, e.Cor, e.Tipo, e.Ativo) = (form.Nome.Trim(), form.Ordem, form.Cor, form.Tipo, form.Ativo);
        await db.SaveChangesAsync();
    }

    public async Task ExcluirEstagioAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        var n = await db.Leads.CountAsync(l => l.EstagioId == id && l.ExcluidoEm == null);
        if (n > 0) throw new RegraNegocioException("_", $"Há {n} lead(s) neste estágio. Mova-os ou desative o estágio em vez de excluir.");
        try { await db.FunilEstagios.Where(e => e.Id == id).ExecuteDeleteAsync(); }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException)
        {
            throw new RegraNegocioException("_", "Estágio com histórico: desative-o em vez de excluir.");
        }
    }

    public async Task ReordenarAsync(UsuarioAtual u, IReadOnlyList<(Guid Id, int Ordem)> itens)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var (id, ordem) in itens)
            await db.FunilEstagios.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Ordem, ordem));
        await tx.CommitAsync();
    }
}
