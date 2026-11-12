using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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
    string? ProximoTitulo = null, DateTime? NaFaseDesde = null, string? ImovelCodigo = null, int OutrosImoveis = 0, decimal? Valor = null,
    decimal? ValorProposta = null, decimal? PropostaAnunciado = null, StatusProposta? StatusProposta = null, Guid? ResponsavelId = null)
{
    public SituacaoProximoPasso Situacao => SituacaoDe(ProximaAcaoEm);

    /// <summary>Lead na fila aberta: ninguém pegou o atendimento ainda.</summary>
    public bool NaFila => ResponsavelId is null;

    /// <summary>Valor da negociação: a proposta em análise ou aceita; sem proposta, o anunciado.</summary>
    public decimal? ValorAtual => ValorProposta ?? Valor;

    public decimal? DiferencaPct => ValorProposta is { } v ? PropostasService.DiferencaPct(v, PropostaAnunciado) : null;

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
    public decimal ValorTotal => Leads.Sum(l => l.ValorAtual ?? 0);
}

/// <summary>Lead interessado num imóvel (aba "Interessados" da ficha do imóvel).</summary>
public sealed record InteressadoImovel(
    Guid LeadId, string PessoaNome, string? Telefone, FinalidadeLead Finalidade, string Estagio, string? EstagioCor, TipoEstagio EstagioTipo,
    DateTime? ProximaAcaoEm, string? ProximoTitulo, string? ResponsavelNome, bool SoSugerido,
    decimal? ValorProposta = null, decimal? PropostaAnunciado = null, StatusProposta? StatusProposta = null)
{
    public SituacaoProximoPasso Situacao => LeadCartao.SituacaoDe(ProximaAcaoEm);

    public decimal? DiferencaPct => ValorProposta is { } v ? PropostasService.DiferencaPct(v, PropostaAnunciado) : null;
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

public sealed record ImovelInteresse(Guid Id, string Codigo, string Titulo, SituacaoImovel Situacao,
    decimal? PrecoVenda = null, decimal? PrecoLocacao = null);

public enum TipoEventoLead { Acao, Atividade, Estagio, Dados }

/// <summary>Item da linha do tempo do lead (ações, atividades, mudanças de estágio e de dados).</summary>
public sealed record EventoLead(DateTime Em, TipoEventoLead Tipo, string Icone, string Titulo, string? Detalhe, string? Usuario);

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

    /// <summary>
    /// REGRA (fila aberta): no funil, o Corretor vê os leads dele e os que estão sem responsável. Os da fila só abrem e
    /// se movem depois que ele clica em "Pegar atendimento" (<see cref="PegarAsync"/>).
    /// </summary>
    private static IQueryable<Lead> VisiveisNoFunil(InovabensDb db, UsuarioAtual u) =>
        u.EscopoProprio ? db.Leads.Where(l => l.ExcluidoEm == null && (l.ResponsavelId == u.Id || l.ResponsavelId == null)) : Visiveis(db, u);

    /* ----------------------------- Pipeline ----------------------------- */

    /// <param name="imovelId">Só os leads interessados neste imóvel (marcado como interesse ou sugerido pelo WhatsApp).</param>
    /// <param name="responsavelId">Só os leads deste responsável.</param>
    /// <param name="semResponsavel">Só a fila aberta (leads sem responsável).</param>
    public async Task<IReadOnlyList<ColunaFunil>> PipelineAsync(UsuarioAtual u, Funil funil, Guid? imovelId = null,
        Guid? responsavelId = null, bool semResponsavel = false)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var estagios = await db.FunilEstagios.AsNoTracking().Where(e => e.Funil == funil && e.Ativo).OrderBy(e => e.Ordem).ToListAsync();
        var finalidade = FinalidadeDo(funil);
        var q = VisiveisNoFunil(db, u).AsNoTracking().Where(l => l.Finalidade == finalidade);
        if (semResponsavel) q = q.Where(l => l.ResponsavelId == null);
        else if (responsavelId is { } r) q = q.Where(l => l.ResponsavelId == r);
        if (imovelId is { } im)
            q = q.Where(l => l.ImovelSugeridoId == im || db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id && i.ImovelId == im));
        var venda = finalidade == FinalidadeLead.COMPRA;
        var linhas = await q
            .OrderBy(l => l.Ordem).ThenByDescending(l => l.AtualizadoEm)
            .Select(l => new
            {
                l.EstagioId, l.Id, Nome = l.Pessoa!.NomeRazao, Telefone = l.Pessoa.Telefone ?? l.Pessoa.Whatsapp, l.Pessoa.Email, l.Finalidade, l.Origem,
                l.ResponsavelId, Responsavel = l.Responsavel != null ? l.Responsavel.Nome : null, l.OrcamentoMax, l.ProximaAcaoEm, l.Ordem, l.CriadoEm,
                SemInteresse = !db.LeadImoveisInteresse.Any(i => i.LeadId == l.Id),
                Sugerido = db.Imoveis.Where(i => i.Id == l.ImovelSugeridoId).Select(i => i.Codigo).FirstOrDefault(),
                Proximo = db.Compromissos.Where(c => c.LeadId == l.Id && c.Status == StatusCompromisso.PENDENTE && c.ExcluidoEm == null)
                    .OrderBy(c => c.InicioEm).Select(c => new { c.InicioEm, c.Titulo }).FirstOrDefault(),
                NaFase = db.LeadHistoricoEstagios.Where(h => h.LeadId == l.Id && h.EstagioNovoId == l.EstagioId).Max(h => (DateTime?)h.CriadoEm),
                Proposta = db.Propostas.Where(p => p.LeadId == l.Id && (p.Status == StatusProposta.EM_ANALISE || p.Status == StatusProposta.ACEITA))
                    .OrderByDescending(p => p.Status == StatusProposta.ACEITA).ThenByDescending(p => p.CriadoEm)
                    .Select(p => new { p.ValorTotal, p.ValorAnunciado, p.Status }).FirstOrDefault(),
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
            l.Imoveis.Max(i => i.Preco), l.Proposta?.ValorTotal, l.Proposta?.ValorAnunciado, l.Proposta?.Status, l.ResponsavelId))).ToList();
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
                Proposta = db.Propostas.Where(p => p.LeadId == l.Id && p.ImovelId == imovelId && (p.Status == StatusProposta.EM_ANALISE || p.Status == StatusProposta.ACEITA))
                    .OrderByDescending(p => p.Status == StatusProposta.ACEITA).ThenByDescending(p => p.CriadoEm)
                    .Select(p => new { p.ValorTotal, p.ValorAnunciado, p.Status }).FirstOrDefault(),
            })
            .ToListAsync();
        return [.. linhas.Select(l => new InteressadoImovel(l.Id, l.Nome, l.Telefone, l.Finalidade, l.Estagio, l.Cor, l.Tipo,
            l.Proximo?.InicioEm ?? l.ProximaAcaoEm, l.Proximo?.Titulo, l.Responsavel, l.SoSugerido,
            l.Proposta?.ValorTotal, l.Proposta?.ValorAnunciado, l.Proposta?.Status))];
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
            .Join(db.Imoveis.Where(im => im.ExcluidoEm == null), i => i.ImovelId, im => im.Id, (i, im) => new ImovelInteresse(im.Id, im.Codigo, im.Titulo, im.Situacao, im.PrecoVenda, im.PrecoLocacao))
            .ToListAsync();
        var sugerido = l.ImovelSugeridoId is { } sid && interesse.All(i => i.Id != sid)
            ? await db.Imoveis.AsNoTracking().Where(im => im.Id == sid && im.ExcluidoEm == null)
                .Select(im => new ImovelInteresse(im.Id, im.Codigo, im.Titulo, im.Situacao, im.PrecoVenda, im.PrecoLocacao)).FirstOrDefaultAsync()
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

    /// <summary>
    /// "Pegar atendimento": o usuário vira o responsável de um lead da fila aberta. Se dois clicarem juntos, só o
    /// primeiro fica com o lead. Os avisos de "novo lead" dos outros corretores se resolvem.
    /// </summary>
    public async Task PegarAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmEditar);
        await using var db = await fabrica.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var pegou = await db.Leads.Where(l => l.Id == id && l.ExcluidoEm == null && l.ResponsavelId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ResponsavelId, u.Id).SetProperty(l => l.AtualizadoEm, DateTime.UtcNow));
        if (pegou == 0)
        {
            var dono = await db.Leads.Where(l => l.Id == id && l.ExcluidoEm == null)
                .Select(l => new { Nome = l.Responsavel != null ? l.Responsavel.Nome : null }).SingleOrDefaultAsync()
                ?? throw new NaoEncontradoException("Lead não encontrado.");
            throw new RegraNegocioException("ResponsavelId", $"Este lead já está com {dono.Nome ?? "outro usuário"}.");
        }
        await db.Alertas.Where(a => a.LeadId == id && a.Tipo == TipoAlerta.NOVO_LEAD && a.ResolvidoEm == null && a.UsuarioId != u.Id
                                    && db.Usuarios.Any(x => x.Id == a.UsuarioId && x.Perfil!.EscopoProprio))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));
        audit.Registrar(db, u.Id, "leads", id, "PEGAR", depois: new { responsavel_id = u.Id });
        await db.SaveChangesAsync();
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
    internal static async Task<Guid?> ProximoPasso(InovabensDb db, UsuarioAtual u, Lead l, ProximoPassoForm p, LeadInteracao? acao)
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
    public async Task<IReadOnlyList<Compromisso>> PendentesDoLeadAsync(UsuarioAtual u, Guid id) =>
        [.. (await AtividadesDoLeadAsync(u, id)).Where(c => c.Status == StatusCompromisso.PENDENTE)];

    /// <summary>Todas as atividades do lead: pendentes primeiro (da mais próxima), depois as encerradas (da mais recente).</summary>
    public async Task<IReadOnlyList<Compromisso>> AtividadesDoLeadAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.CrmVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Detalhe(db, u, id);
        var todas = await db.Compromissos.AsNoTracking().Where(c => c.LeadId == id && c.ExcluidoEm == null).ToListAsync();
        return [.. todas.Where(c => c.Status == StatusCompromisso.PENDENTE).OrderBy(c => c.InicioEm),
            .. todas.Where(c => c.Status != StatusCompromisso.PENDENTE).OrderByDescending(c => c.ConcluidoEm ?? c.AtualizadoEm)];
    }

    /// <summary>Campos do lead que aparecem como "dados alterados" na linha do tempo (chaves do JSON da auditoria).</summary>
    private static readonly (string Chave, string Rotulo)[] CamposAuditados =
    [
        ("origem", "origem"), ("orcamento_min", "orçamento mínimo"), ("orcamento_max", "orçamento máximo"),
        ("observacoes", "observações"), ("responsavel_id", "responsável"),
    ];

    internal static IReadOnlyList<string> CamposAlterados(string? antes, string? depois)
    {
        if (antes is null || depois is null) return [];
        try
        {
            using var a = JsonDocument.Parse(antes);
            using var d = JsonDocument.Parse(depois);
            static string? V(JsonDocument doc, string k) =>
                doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(k, out var p) ? p.ToString() : null;
            if (V(d, "estagio") is not null) return []; // mudança de estágio: já vem do histórico
            return [.. CamposAuditados.Where(c => V(a, c.Chave) != V(d, c.Chave)).Select(c => c.Rotulo)];
        }
        catch (JsonException) { return []; }
    }

    private static string IconeCanal(CanalInteracao c) => c switch
    {
        CanalInteracao.TELEFONE => "📞",
        CanalInteracao.WHATSAPP => "💬",
        CanalInteracao.EMAIL => "✉️",
        CanalInteracao.VISITA => "🏠",
        _ => "📝",
    };

    /// <summary>Linha do tempo do lead, do mais recente para o mais antigo.</summary>
    public async Task<IReadOnlyList<EventoLead>> LinhaDoTempoAsync(UsuarioAtual u, Guid id)
    {
        var historico = await HistoricoAsync(u, id); // também confere o acesso ao lead
        await using var db = await fabrica.CreateDbContextAsync();
        var nomes = await db.Usuarios.AsNoTracking().Select(x => new { x.Id, x.Nome }).ToDictionaryAsync(x => x.Id, x => x.Nome);
        string? Nome(Guid? uid) => uid is { } g ? nomes.GetValueOrDefault(g) : null;
        var eventos = new List<EventoLead>();

        var compromissos = await db.Compromissos.AsNoTracking().Where(c => c.LeadId == id && c.ExcluidoEm == null).ToDictionaryAsync(c => c.Id);
        var interacoes = await db.LeadInteracoes.AsNoTracking().Where(i => i.LeadId == id).ToListAsync();
        foreach (var i in interacoes)
        {
            var detalhe = i.ProximoCompromissoId is { } pc && compromissos.TryGetValue(pc, out var c)
                ? $"Próximo passo: {c.Titulo} · {Texto.DataHora(c.InicioEm)}"
                : i.MotivoSemProximo is { } m ? $"Sem próximo passo: {m}" : null;
            if (i.CompromissoConcluidoId is { } cc && compromissos.TryGetValue(cc, out var feito))
                detalhe = $"Concluiu: {feito.Titulo}" + (detalhe is null ? "" : $" · {detalhe}");
            eventos.Add(new EventoLead(i.CriadoEm, TipoEventoLead.Acao, i.PropostaId is null ? IconeCanal(i.Canal) : "💰", i.Resumo, detalhe,
                Nome(i.UsuarioId) ?? (i.Canal == CanalInteracao.WHATSAPP ? "automático" : null)));
        }

        // atividades agendadas fora de uma ação ("+" do cartão, agenda) e as encerradas sem ação de resultado
        var agendadasEmAcao = interacoes.Where(i => i.ProximoCompromissoId != null).Select(i => i.ProximoCompromissoId!.Value).ToHashSet();
        var concluidasEmAcao = interacoes.Where(i => i.CompromissoConcluidoId != null).Select(i => i.CompromissoConcluidoId!.Value).ToHashSet();
        foreach (var c in compromissos.Values)
        {
            if (!agendadasEmAcao.Contains(c.Id))
                eventos.Add(new EventoLead(c.CriadoEm, TipoEventoLead.Atividade, "🗓️", $"Atividade agendada: {c.Titulo}",
                    Texto.DataHora(c.InicioEm), Nome(c.ResponsavelId)));
            if (c.Status == StatusCompromisso.CONCLUIDO && !concluidasEmAcao.Contains(c.Id))
                eventos.Add(new EventoLead(c.ConcluidoEm ?? c.AtualizadoEm, TipoEventoLead.Atividade, "✔️", $"Atividade concluída: {c.Titulo}", null, Nome(c.ResponsavelId)));
            if (c.Status == StatusCompromisso.CANCELADO)
                eventos.Add(new EventoLead(c.AtualizadoEm, TipoEventoLead.Atividade, "✖️", $"Atividade cancelada: {c.Titulo}", null, null));
        }

        foreach (var h in historico)
            eventos.Add(new EventoLead(h.CriadoEm, TipoEventoLead.Estagio, "➡️",
                h.EstagioAnterior is null ? $"Lead criado em {h.EstagioNovo}" : $"{h.EstagioAnterior} → {h.EstagioNovo}", null, h.Usuario));

        var auditoria = await db.AuditLog.AsNoTracking().Where(a => a.Entidade == "leads" && a.EntidadeId == id && a.Acao == "ATUALIZAR")
            .Select(a => new { a.CriadoEm, a.UsuarioId, a.ValoresAntes, a.ValoresDepois }).ToListAsync();
        foreach (var a in auditoria)
            if (CamposAlterados(a.ValoresAntes, a.ValoresDepois) is { Count: > 0 } campos)
                eventos.Add(new EventoLead(a.CriadoEm, TipoEventoLead.Dados, "✏️", "Dados alterados", string.Join(", ", campos), Nome(a.UsuarioId)));

        return [.. eventos.OrderByDescending(e => e.Em)];
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
