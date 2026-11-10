using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Agenda;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

/// <summary>Situação do próximo passo do lead, mostrada no cartão do funil.</summary>
public enum SituacaoProximoPasso { Atrasado, Hoje, Agendado, Nenhum }

/// <param name="ProximaAcaoEm">Início da próxima atividade pendente (UTC), ou a "próxima ação" antiga do lead.</param>
/// <param name="NaFaseDesde">Quando o lead entrou no estágio atual (UTC).</param>
/// <param name="Valor">Valor anunciado do imóvel de interesse (o maior, se houver mais de um).</param>
public sealed record LeadCartao(
    Guid Id, string PessoaNome, string? Telefone, string? Email, FinalidadeLead Finalidade, string? Origem,
    string? ResponsavelNome, decimal? OrcamentoMax, DateTime? ProximaAcaoEm, int Ordem,
    bool VincularImovel = false, string? ImovelSugerido = null,
    string? ProximoTitulo = null, DateTime? NaFaseDesde = null, string? ImovelCodigo = null, int OutrosImoveis = 0, decimal? Valor = null)
{
    public SituacaoProximoPasso Situacao => SituacaoDe(ProximaAcaoEm);

    public int DiasNaFase => NaFaseDesde is { } d ? Math.Max(0, DatasBR.Hoje().DayNumber - DateOnly.FromDateTime(DatasBR.DeUtc(d)).DayNumber) : 0;

    public static SituacaoProximoPasso SituacaoDe(DateTime? utc) => utc switch
    {
        null => SituacaoProximoPasso.Nenhum,
        { } p when p < DateTime.UtcNow => SituacaoProximoPasso.Atrasado,
        { } p when DateOnly.FromDateTime(DatasBR.DeUtc(p)) == DatasBR.Hoje() => SituacaoProximoPasso.Hoje,
        _ => SituacaoProximoPasso.Agendado,
    };
}

public sealed record ColunaFunil(FunilEstagio Estagio, IReadOnlyList<LeadCartao> Leads)
{
    public decimal ValorTotal => Leads.Sum(l => l.Valor ?? 0);
}

/// <summary>Lead interessado num imóvel (aba "Interessados" da ficha do imóvel).</summary>
public sealed record InteressadoImovel(
    Guid LeadId, string PessoaNome, string? Telefone, FinalidadeLead Finalidade, string Estagio, string? EstagioCor, TipoEstagio EstagioTipo,
    DateTime? ProximaAcaoEm, string? ProximoTitulo, string? ResponsavelNome, bool SoSugerido)
{
    public SituacaoProximoPasso Situacao => LeadCartao.SituacaoDe(ProximaAcaoEm);
}

/// <summary>Atividade pendente da agenda (tela "Minhas atividades").</summary>
public sealed record AtividadeItem(
    Guid Id, TipoCompromisso Tipo, string Titulo, string? Descricao, DateTime InicioEm, DateTime FimEm, string ResponsavelNome,
    Guid? LeadId, string? LeadNome, string? Telefone, string? Whatsapp, Guid? ImovelId, string? ImovelCodigo)
{
    public SituacaoProximoPasso Situacao => LeadCartao.SituacaoDe(InicioEm);
}

public sealed record LeadDetalhe(Lead Dados, Pessoa Pessoa, FunilEstagio Estagio, string? ResponsavelNome, IReadOnlyList<ImovelInteresse> ImoveisInteresse,
    ImovelInteresse? ImovelSugerido = null)
{
    /// <summary>Lead que chegou pelo WhatsApp e ainda não tem imóvel de interesse.</summary>
    public bool VincularImovel => Dados.Origem == WhatsappService.Origem && ImoveisInteresse.Count == 0;
}

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
    [StringLength(4000, ErrorMessage = "No máximo 4000 caracteres.")] public string? Observacoes { get; set; }

    /// <summary>Imóveis de interesse gravados junto com o lead; null = não altera os atuais.</summary>
    public List<Guid>? ImoveisInteresse { get; set; }

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
        l.Observacoes = Texto.Limpo(Observacoes);
    }
}

/// <summary>
/// REGRA: toda ação registrada no lead pede o próximo passo, com data, que vira compromisso na agenda.
/// Pode ser pulado informando o motivo. Datas em horário de Brasília.
/// </summary>
public class ProximoPassoForm : IValidatableObject
{
    public bool Pular { get; set; }
    [StringLength(500, ErrorMessage = "No máximo 500 caracteres.")] public string? MotivoPular { get; set; }

    public TipoCompromisso Tipo { get; set; } = TipoCompromisso.LIGACAO;
    [StringLength(200, ErrorMessage = "No máximo 200 caracteres.")] public string? Titulo { get; set; }
    public DateTime? Inicio { get; set; }
    [Range(5, 720, ErrorMessage = "Duração de 5 a 720 minutos.")] public int DuracaoMin { get; set; } = 30;
    /// <summary>Imóvel da atividade; vazio = o único imóvel de interesse do lead, se houver só um.</summary>
    public Guid? ImovelId { get; set; }

    public virtual IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Pular)
        {
            if ((Texto.Limpo(MotivoPular)?.Length ?? 0) < 3)
                yield return new ValidationResult("Informe por que não vai agendar o próximo passo", [nameof(MotivoPular)]);
            yield break;
        }
        if ((Texto.Limpo(Titulo)?.Length ?? 0) < 2) yield return new ValidationResult("Informe o próximo passo", [nameof(Titulo)]);
        if (Inicio is not { } i) yield return new ValidationResult("Informe a data do próximo passo", [nameof(Inicio)]);
        else if (i < DatasBR.Agora().AddMinutes(-5)) yield return new ValidationResult("A data do próximo passo já passou", [nameof(Inicio)]);
    }

    /// <summary>Sugestão: amanhã às 9 h.</summary>
    public static DateTime SugestaoInicio() => DatasBR.Agora().Date.AddDays(1).AddHours(9);

    public void Limpar(TipoCompromisso tipo = TipoCompromisso.LIGACAO)
    {
        (Pular, MotivoPular, Tipo, Titulo, Inicio, DuracaoMin, ImovelId) = (false, null, tipo, null, SugestaoInicio(), 30, null);
    }
}

public sealed class InteracaoForm : ProximoPassoForm
{
    public InteracaoForm() => Inicio = SugestaoInicio();

    public CanalInteracao Canal { get; set; } = CanalInteracao.WHATSAPP;
    [Required(ErrorMessage = "Descreva o contato."), MinLength(2, ErrorMessage = "Descreva o contato.")]
    public string Resumo { get; set; } = "";

    /// <summary>Atividade da agenda que esta ação conclui (o resumo é o resultado).</summary>
    public Guid? ConcluirCompromissoId { get; set; }
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
public sealed class CrmService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, GoogleService google)
{
    /// <summary>REGRA: imóvel vendido ou arquivado não pode ser marcado como novo interesse (os já marcados continuam).</summary>
    public static readonly IReadOnlyList<SituacaoImovel> SituacoesSemInteresse = [SituacaoImovel.VENDIDO, SituacaoImovel.ARQUIVADO];

    public static Funil FunilDa(FinalidadeLead f) => f == FinalidadeLead.COMPRA ? Funil.VENDA : Funil.LOCACAO;
    private static FinalidadeLead FinalidadeDo(Funil f) => f == Funil.VENDA ? FinalidadeLead.COMPRA : FinalidadeLead.LOCACAO;

    private static IQueryable<Lead> Visiveis(InovabensDb db, UsuarioAtual u) =>
        db.Leads.Where(l => l.ExcluidoEm == null).VisiveisPara(u);

    /* ----------------------------- Pipeline ----------------------------- */

    /// <param name="imovelId">Só os leads interessados neste imóvel (marcado como interesse ou sugerido pelo WhatsApp).</param>
    public async Task<IReadOnlyList<ColunaFunil>> PipelineAsync(UsuarioAtual u, Funil funil, Guid? imovelId = null)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var estagios = await db.FunilEstagios.AsNoTracking().Where(e => e.Funil == funil && e.Ativo).OrderBy(e => e.Ordem).ToListAsync();
        var finalidade = FinalidadeDo(funil);
        var q = Visiveis(db, u).AsNoTracking().Where(l => l.Finalidade == finalidade);
        if (imovelId is { } im)
            q = q.Where(l => l.ImovelSugeridoId == im || db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id && i.ImovelId == im));
        var venda = finalidade == FinalidadeLead.COMPRA;
        var linhas = await q
            .OrderBy(l => l.Ordem).ThenByDescending(l => l.AtualizadoEm)
            .Select(l => new
            {
                l.EstagioId, l.Id, Nome = l.Pessoa!.NomeRazao, Telefone = l.Pessoa.Telefone ?? l.Pessoa.Whatsapp, l.Pessoa.Email, l.Finalidade, l.Origem,
                Responsavel = l.Responsavel != null ? l.Responsavel.Nome : null, l.OrcamentoMax, l.ProximaAcaoEm, l.Ordem, l.CriadoEm,
                SemInteresse = !db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id),
                Sugerido = db.Imoveis.Where(i => i.Id == l.ImovelSugeridoId).Select(i => i.Codigo).FirstOrDefault(),
                Proximo = db.Compromissos.Where(c => c.LeadId == l.Id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
                    .OrderBy(c => c.InicioEm).Select(c => new { c.InicioEm, c.Titulo }).FirstOrDefault(),
                NaFase = db.LeadHistoricoEstagios.Where(h => h.LeadId == l.Id && h.EstagioNovoId == l.EstagioId).Max(h => (DateTime?)h.CriadoEm),
                Imoveis = (from i in db.LeadImoveisInteresse
                           where i.LeadId == l.Id
                           join imv in db.Imoveis on i.ImovelId equals imv.Id
                           where imv.ExcluidoEm == null
                           orderby imv.Codigo
                           select new { imv.Codigo, Preco = venda ? imv.PrecoVenda : imv.PrecoLocacao }).ToList(),
            })
            .ToListAsync();
        var cartoes = linhas.Select(l => (l.EstagioId, Cartao: new LeadCartao(
            l.Id, l.Nome, l.Telefone, l.Email, l.Finalidade, l.Origem, l.Responsavel, l.OrcamentoMax,
            l.Proximo?.InicioEm ?? l.ProximaAcaoEm, l.Ordem,
            l.Origem == WhatsappService.Origem && l.SemInteresse, l.Sugerido,
            l.Proximo?.Titulo, l.NaFase ?? l.CriadoEm, l.Imoveis.FirstOrDefault()?.Codigo, Math.Max(0, l.Imoveis.Count - 1),
            l.Imoveis.Max(i => i.Preco)))).ToList();
        return [.. estagios.Select(e => new ColunaFunil(e, [.. cartoes.Where(l => l.EstagioId == e.Id).Select(l => l.Cartao)]))];
    }

    /// <summary>Leads interessados no imóvel, de todos os funis (aba "Interessados" da ficha).</summary>
    public async Task<IReadOnlyList<InteressadoImovel>> InteressadosAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var linhas = await Visiveis(db, u).AsNoTracking()
            .Where(l => l.ImovelSugeridoId == imovelId || db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id && i.ImovelId == imovelId))
            .OrderBy(l => l.Estagio!.Funil).ThenBy(l => l.Estagio!.Ordem).ThenBy(l => l.Ordem)
            .Select(l => new
            {
                l.Id, Nome = l.Pessoa!.NomeRazao, Telefone = l.Pessoa.Telefone ?? l.Pessoa.Whatsapp, l.Finalidade,
                Estagio = l.Estagio!.Nome, l.Estagio.Cor, l.Estagio.Tipo, l.ProximaAcaoEm,
                Proximo = db.Compromissos.Where(c => c.LeadId == l.Id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
                    .OrderBy(c => c.InicioEm).Select(c => new { c.InicioEm, c.Titulo }).FirstOrDefault(),
                Responsavel = l.Responsavel != null ? l.Responsavel.Nome : null,
                SoSugerido = !db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id && i.ImovelId == imovelId),
            })
            .ToListAsync();
        return [.. linhas.Select(l => new InteressadoImovel(l.Id, l.Nome, l.Telefone, l.Finalidade, l.Estagio, l.Cor, l.Tipo,
            l.Proximo?.InicioEm ?? l.ProximaAcaoEm, l.Proximo?.Titulo, l.Responsavel, l.SoSugerido))];
    }

    /// <summary>Atividades pendentes da agenda (com o contato do lead), as do próprio usuário ou de todos.</summary>
    public async Task<IReadOnlyList<AtividadeItem>> AtividadesAsync(UsuarioAtual u, bool todas = false, int diasAFrente = 30)
    {
        u.Exigir(P.AgendaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var limite = DatasBR.ParaUtc(DatasBR.Agora().Date.AddDays(diasAFrente + 1));
        var q = db.Compromissos.AsNoTracking()
            .Where(c => c.ExcluidoEm == null && c.Status == StatusCompromisso.PENDENTE && c.InicioEm < limite
                        && (c.ImovelId == null || c.Imovel!.ExcluidoEm == null))
            .VisiveisPara(u);
        if (!todas || u.EscopoProprio) q = q.Where(c => c.ResponsavelId == u.Id);
        var linhas = await q.OrderBy(c => c.InicioEm)
            .Select(c => new
            {
                c.Id, c.Tipo, c.Titulo, c.Descricao, c.InicioEm, c.FimEm, Resp = c.Responsavel!.Nome, c.LeadId, c.ImovelId,
                ImovelCodigo = c.Imovel != null ? c.Imovel.Codigo : null,
                Lead = db.Leads.Where(l => l.Id == c.LeadId && l.ExcluidoEm == null)
                    .Select(l => new { l.Pessoa!.NomeRazao, l.Pessoa.Telefone, l.Pessoa.Whatsapp }).FirstOrDefault(),
            })
            .ToListAsync();
        return [.. linhas.Select(c => new AtividadeItem(c.Id, c.Tipo, c.Titulo, c.Descricao, c.InicioEm, c.FimEm, c.Resp,
            c.Lead is null ? null : c.LeadId, c.Lead?.NomeRazao, c.Lead?.Telefone, c.Lead?.Whatsapp ?? c.Lead?.Telefone, c.ImovelId, c.ImovelCodigo))];
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
            .Join(db.Imoveis.Where(im => im.ExcluidoEm == null), i => i.ImovelId, im => im.Id, (i, im) => new ImovelInteresse(im.Id, im.Codigo, im.Titulo, im.Situacao))
            .ToListAsync();
        var sugerido = l.ImovelSugeridoId is { } sid && interesse.All(i => i.Id != sid)
            ? await db.Imoveis.AsNoTracking().Where(im => im.Id == sid && im.ExcluidoEm == null)
                .Select(im => new ImovelInteresse(im.Id, im.Codigo, im.Titulo, im.Situacao)).FirstOrDefaultAsync()
            : null;
        return new LeadDetalhe(l, l.Pessoa!, l.Estagio!, l.Responsavel?.Nome, interesse, sugerido);
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
                $"insert into pessoa_papeis (pessoa_id, papel) values ({pid}, {PerfilPessoa.Cliente}) on conflict do nothing");
        }
        else
        {
            var p = new Pessoa
            {
                Id = Guid.NewGuid(), NomeRazao = form.NovoNome!.Trim(), Telefone = Texto.Limpo(form.NovoTelefone),
                Email = Texto.Limpo(form.NovoEmail)?.ToLowerInvariant(), CriadoPor = u.Id,
            };
            p.Papeis.Add(new PessoaPapel { PessoaId = p.Id, Papel = PerfilPessoa.Cliente });
            db.Pessoas.Add(p);
            pessoaId = p.Id;
        }

        var l = new Lead
        {
            Id = Guid.NewGuid(), PessoaId = pessoaId, Finalidade = form.Finalidade, EstagioId = estagio.Id,
            ResponsavelId = Acesso.ResponsavelAoCriar(u, form.ResponsavelId),
        };
        form.AplicarEm(l);
        db.Leads.Add(l);
        db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = l.Id, EstagioNovoId = estagio.Id, UsuarioId = u.Id });
        audit.Registrar(db, u.Id, "leads", l.Id, "CRIAR", depois: l);
        await db.SaveChangesAsync();
        if (form.ImoveisInteresse is { } interesse) await SincronizarInteresse(db, u, l.Id, interesse);
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
        if (Acesso.NovoResponsavel(u, form.ResponsavelId) is { } r) l.ResponsavelId = r;
        audit.Registrar(db, u.Id, "leads", id, "ATUALIZAR", antes, l);
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();
        if (form.ImoveisInteresse is { } interesse) await SincronizarInteresse(db, u, id, interesse);
        await tx.CommitAsync();
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

    /// <summary>
    /// Move o lead no funil e registra o histórico. REGRAS: Perdido exige motivo, não tem próximo passo e cancela as
    /// atividades pendentes do lead. Nos demais (inclusive Ganho) a tela pede o próximo passo (<paramref name="proximo"/>),
    /// que é gravado como uma ação "mudou de estágio".
    /// </summary>
    public async Task MoverAsync(UsuarioAtual u, Guid id, Guid estagioId, string? motivoPerda = null, int? ordem = null,
        ProximoPassoForm? proximo = null)
    {
        u.Exigir(P.CrmEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).Include(x => x.Estagio).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new NaoEncontradoException("Lead não encontrado.");
        var novo = await EstagioDoFunil(db, estagioId, FunilDa(l.Finalidade));
        var motivo = MotivoPerda(novo.Tipo, motivoPerda);
        if (novo.Tipo == TipoEstagio.PERDIDO) proximo = null; // Ganho segue com passos (assinatura, escritura…)
        if (proximo is not null) Validacao.Validar(proximo);

        var anterior = l.Estagio!;
        var paraGoogle = new List<Guid>();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (novo.Tipo == TipoEstagio.PERDIDO && anterior.Id != novo.Id)
        {
            paraGoogle = await db.Compromissos.Where(c => c.LeadId == id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
                .Select(c => c.Id).ToListAsync();
            await db.Compromissos.Where(c => paraGoogle.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, StatusCompromisso.CANCELADO).SetProperty(c => c.AtualizadoEm, DateTime.UtcNow));
            foreach (var c in paraGoogle) await AlertasService.ResolverDoCompromisso(db, c);
            l.ProximaAcaoEm = null;
        }
        if (proximo is not null && anterior.Id != novo.Id)
        {
            var acao = new LeadInteracao { LeadId = id, UsuarioId = u.Id, Canal = CanalInteracao.OUTRO, Resumo = $"Mudou de estágio: {anterior.Nome} → {novo.Nome}" };
            if (await ProximoPasso(db, u, l, proximo, acao) is { } c) paraGoogle.Add(c);
            db.LeadInteracoes.Add(acao);
        }
        var prox = (await db.Leads.Where(x => x.EstagioId == novo.Id).MaxAsync(x => (int?)x.Ordem) ?? -1) + 1;
        // posição escolhida (soltar o cartão sobre outro): abre espaço empurrando os de baixo
        if (ordem is { } pos)
            await db.Leads.Where(x => x.EstagioId == novo.Id && x.Id != id && x.Ordem >= pos)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Ordem, x => x.Ordem + 1));
        l.EstagioId = novo.Id;
        l.Estagio = novo;
        l.Ordem = ordem ?? prox;
        l.MotivoPerda = motivo;
        if (novo.Id != anterior.Id)
            db.LeadHistoricoEstagios.Add(new LeadHistoricoEstagio { LeadId = id, EstagioAnteriorId = anterior.Id, EstagioNovoId = novo.Id, UsuarioId = u.Id });
        audit.Registrar(db, u.Id, "leads", id, "ATUALIZAR", new { estagio = anterior.Nome }, new { estagio = novo.Nome, motivo_perda = motivo });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        foreach (var c in paraGoogle) google.Empurrar(c);
    }

    /// <summary>
    /// Grava o próximo passo de uma ação: cria o compromisso na agenda (responsável = o do lead) ou guarda o motivo de
    /// ter pulado. Devolve o id do compromisso criado. Quem chama salva e abre a transação.
    /// </summary>
    private static async Task<Guid?> ProximoPasso(InovabensDb db, UsuarioAtual u, Lead l, ProximoPassoForm p, LeadInteracao? acao)
    {
        if (p.Pular)
        {
            if (acao is not null) acao.MotivoSemProximo = Texto.Limpo(p.MotivoPular);
            return null;
        }
        var imovelId = p.ImovelId;
        if (imovelId is { } im)
        {
            var ok = await db.LeadImoveisInteresse.AnyAsync(i => i.LeadId == l.Id && i.ImovelId == im)
                     || await db.Imoveis.Where(i => i.Id == im && i.ExcluidoEm == null).VisiveisPara(u).AnyAsync();
            if (!ok) throw new NaoEncontradoException("Imóvel não encontrado.");
        }
        else
        {
            var interesse = await db.LeadImoveisInteresse.Where(i => i.LeadId == l.Id).Select(i => i.ImovelId).Take(2).ToListAsync();
            if (interesse.Count == 1) imovelId = interesse[0];
        }
        var inicio = DatasBR.ParaUtc(p.Inicio!.Value);
        var c = new Compromisso
        {
            Id = Guid.NewGuid(), LeadId = l.Id, ImovelId = imovelId, Tipo = p.Tipo, Titulo = p.Titulo!.Trim(),
            InicioEm = inicio, FimEm = inicio.AddMinutes(p.DuracaoMin),
            ResponsavelId = Acesso.ResponsavelAoCriar(u, l.ResponsavelId),
        };
        db.Compromissos.Add(c);
        if (acao is not null) acao.ProximoCompromissoId = c.Id;
        var proxima = await db.Compromissos.Where(x => x.LeadId == l.Id && x.Status == StatusCompromisso.PENDENTE && x.ExcluidoEm == null)
            .MinAsync(x => (DateTime?)x.InicioEm);
        l.ProximaAcaoEm = proxima is { } pr && pr < inicio ? pr : inicio;
        return c.Id;
    }

    /// <summary>Agenda uma atividade para o lead (botão "+" do cartão); aqui não dá para pular.</summary>
    public async Task<Guid> AgendarAsync(UsuarioAtual u, Guid id, ProximoPassoForm form)
    {
        u.Exigir(P.CrmEditar);
        form.Pular = false;
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Lead não encontrado.");
        await using var tx = await db.Database.BeginTransactionAsync();
        var c = (await ProximoPasso(db, u, l, form, null))!.Value;
        l.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        google.Empurrar(c);
        return c;
    }

    /// <summary>Atividades pendentes do lead (para escolher qual concluir ao registrar o resultado).</summary>
    public async Task<IReadOnlyList<Compromisso>> PendentesDoLeadAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Detalhe(db, u, id);
        return await db.Compromissos.AsNoTracking()
            .Where(c => c.LeadId == id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
            .OrderBy(c => c.InicioEm).ToListAsync();
    }

    /// <summary>REGRA: mover para um estágio de perda exige motivo (até 500 caracteres); nos demais ele é descartado.</summary>
    internal static string? MotivoPerda(TipoEstagio destino, string? informado)
    {
        var motivo = Texto.Limpo(informado);
        if (destino != TipoEstagio.PERDIDO) return null;
        if (motivo is null) throw new RegraNegocioException("MotivoPerda", "Informe o motivo da perda");
        if (motivo.Length > 500) throw new RegraNegocioException("MotivoPerda", "No máximo 500 caracteres");
        return motivo;
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

    /// <summary>
    /// Registra uma ação no lead com o próximo passo (ou o motivo de pular). Com <see cref="InteracaoForm.ConcluirCompromissoId"/>,
    /// conclui essa atividade da agenda e o resumo vira o resultado dela.
    /// </summary>
    public async Task CriarInteracaoAsync(UsuarioAtual u, Guid id, InteracaoForm form)
    {
        u.Exigir(P.CrmEditar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        var l = await Visiveis(db, u).SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Lead não encontrado.");
        var acao = new LeadInteracao { LeadId = id, UsuarioId = u.Id, Canal = form.Canal, Resumo = form.Resumo.Trim() };
        var paraGoogle = new List<Guid>();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (form.ConcluirCompromissoId is { } cid)
        {
            var c = await db.Compromissos.SingleOrDefaultAsync(x => x.Id == cid && x.LeadId == id && x.ExcluidoEm == null)
                ?? throw new NaoEncontradoException("Atividade não encontrada.");
            if (c.Status != StatusCompromisso.PENDENTE) throw new RegraNegocioException(nameof(form.ConcluirCompromissoId), "Esta atividade já foi encerrada");
            var antes = c.Copia();
            c.Status = StatusCompromisso.CONCLUIDO;
            c.ConcluidoEm = c.AtualizadoEm = DateTime.UtcNow;
            audit.Registrar(db, u.Id, "compromissos", c.Id, "ATUALIZAR", antes, c);
            acao.CompromissoConcluidoId = c.Id;
            paraGoogle.Add(c.Id);
            await AlertasService.ResolverDoCompromisso(db, c.Id);
        }
        if (await ProximoPasso(db, u, l, form, acao) is { } novo) paraGoogle.Add(novo);
        else if (form.ConcluirCompromissoId is { } concluido)
            l.ProximaAcaoEm = await db.Compromissos.Where(x => x.LeadId == id && x.Id != concluido && x.Status == StatusCompromisso.PENDENTE && x.ExcluidoEm == null)
                .MinAsync(x => (DateTime?)x.InicioEm);
        db.LeadInteracoes.Add(acao);
        l.AtualizadoEm = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        foreach (var c in paraGoogle) google.Empurrar(c);
    }

    public async Task DefinirInteresseAsync(UsuarioAtual u, Guid id, IReadOnlyCollection<Guid> imoveis)
    {
        u.Exigir(P.CrmEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await Detalhe(db, u, id);
        await using var tx = await db.Database.BeginTransactionAsync();
        await SincronizarInteresse(db, u, id, imoveis);
        await tx.CommitAsync();
    }

    /// <summary>Substitui os imóveis de interesse do lead. Quem chama abre a transação.</summary>
    private static async Task SincronizarInteresse(InovabensDb db, UsuarioAtual u, Guid id, IReadOnlyCollection<Guid> imoveis)
    {
        if (imoveis.Count > 100) throw new RegraNegocioException(nameof(LeadForm.ImoveisInteresse), "No máximo 100 imóveis de interesse");
        // Novos vínculos só com imóveis que o usuário enxerga (Corretor: os seus); senão o vínculo revelaria código e
        // título de imóvel alheio. Os já vinculados (por exemplo, por um gestor, ou antes de o imóvel ser vendido)
        // podem continuar na lista.
        var ids = imoveis.Distinct().ToList();
        var atuais = await db.LeadImoveisInteresse.Where(i => i.LeadId == id).Select(i => i.ImovelId).ToListAsync();
        var novos = ids.Except(atuais).ToList();
        var situacoes = await db.Imoveis.Where(i => novos.Contains(i.Id) && i.ExcluidoEm == null).VisiveisPara(u)
            .Select(i => i.Situacao).ToListAsync();
        if (situacoes.Count != novos.Count) throw new NaoEncontradoException("Imóvel não encontrado.");
        if (situacoes.Any(SituacoesSemInteresse.Contains))
            throw new RegraNegocioException(nameof(LeadForm.ImoveisInteresse), "Imóvel vendido ou arquivado não pode ser marcado como interesse");
        await db.LeadImoveisInteresse.Where(i => i.LeadId == id).ExecuteDeleteAsync();
        db.LeadImoveisInteresse.AddRange(ids.Select(im => new LeadImovelInteresse { LeadId = id, ImovelId = im }));
        await db.SaveChangesAsync();
        if (ids.Count > 0) await WhatsappService.ResolverAlertasDoLead(db, id);
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
