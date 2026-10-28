using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Imoveis;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Agenda;

public sealed record CompromissoDetalhe(
    Compromisso Dados, string ImovelCodigo, string ImovelTitulo, string ResponsavelNome, IReadOnlyList<string> Participantes)
{
    /// <summary>Pendente e já terminou: "não cumprido" (gera alerta pelo job).</summary>
    public bool Vencido => Dados.Status == StatusCompromisso.PENDENTE && Dados.FimEm < DateTime.UtcNow;
}

public sealed record FiltroAgenda(DateTime? De = null, DateTime? Ate = null, Guid? ImovelId = null, Guid? ResponsavelId = null, StatusCompromisso? Status = null);

/// <summary>Datas em horário de Brasília (como digitadas na tela); o serviço converte para UTC.</summary>
public sealed class CompromissoForm : IValidatableObject
{
    public TipoCompromisso Tipo { get; set; } = TipoCompromisso.VISITA;

    [Required(ErrorMessage = "Informe o título.")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "O título deve ter de 2 a 200 caracteres.")]
    public string Titulo { get; set; } = "";

    public string? Descricao { get; set; }
    [StringLength(200)] public string? Local { get; set; }

    [Required(ErrorMessage = "Informe o início.")] public DateTime? Inicio { get; set; }
    [Required(ErrorMessage = "Informe o término.")] public DateTime? Fim { get; set; }

    public Guid? ResponsavelId { get; set; }
    public Guid? LeadId { get; set; }

    /// <summary>E-mails separados por vírgula, ponto e vírgula ou espaço.</summary>
    public string? Convidados { get; set; }

    internal IReadOnlyList<string> Emails =>
        [.. (Convidados ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim().ToLowerInvariant()).Distinct()];

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Inicio is { } i && Fim is { } f && f < i)
            yield return new ValidationResult("O término deve ser depois do início", [nameof(Fim)]);
        var email = new EmailAddressAttribute();
        if (Emails.Count > 30) yield return new ValidationResult("No máximo 30 convidados", [nameof(Convidados)]);
        foreach (var e in Emails.Where(e => !email.IsValid(e)))
            yield return new ValidationResult($"E-mail inválido: {e}", [nameof(Convidados)]);
    }

    /// <summary>Formulário com o próximo horário cheio + 1 h (como a tela Node).</summary>
    public static CompromissoForm Novo()
    {
        var agora = DatasBR.Agora();
        var h = new DateTime(agora.Year, agora.Month, agora.Day, agora.Hour, 0, 0).AddHours(1);
        return new CompromissoForm { Inicio = h, Fim = h.AddHours(1) };
    }

    public static CompromissoForm De(CompromissoDetalhe c) => new()
    {
        Tipo = c.Dados.Tipo, Titulo = c.Dados.Titulo, Descricao = c.Dados.Descricao, Local = c.Dados.Local,
        Inicio = ParaBR(c.Dados.InicioEm), Fim = ParaBR(c.Dados.FimEm), ResponsavelId = c.Dados.ResponsavelId,
        LeadId = c.Dados.LeadId, Convidados = string.Join(", ", c.Participantes),
    };

    public static DateTime ParaBR(DateTime utc) => DatasBR.DeUtc(utc);
}

/// <summary>Compromissos dos imóveis (port de agenda.service.ts). Cada alteração é enviada ao Google em segundo plano.</summary>
public sealed class AgendaService(IDbContextFactory<InovabensDb> fabrica, AuditService audit, GoogleService google)
{
    private static IQueryable<Compromisso> Visiveis(InovabensDb db, UsuarioAtual u) =>
        db.Compromissos.AsNoTracking().Where(c => c.ExcluidoEm == null && c.Imovel!.ExcluidoEm == null).VisiveisPara(u);

    private static async Task<List<CompromissoDetalhe>> Detalhar(InovabensDb db, IQueryable<Compromisso> q)
    {
        var linhas = await q.OrderBy(c => c.InicioEm)
            .Select(c => new { c, c.Imovel!.Codigo, ImovelTitulo = c.Imovel.Titulo, Resp = c.Responsavel!.Nome })
            .ToListAsync();
        var ids = linhas.Select(l => l.c.Id).ToList();
        var parts = await db.CompromissoParticipantes.AsNoTracking().Where(p => ids.Contains(p.CompromissoId) && p.Email != null)
            .Select(p => new { p.CompromissoId, p.Email }).ToListAsync();
        return [.. linhas.Select(l => new CompromissoDetalhe(l.c, l.Codigo, l.ImovelTitulo, l.Resp,
            [.. parts.Where(p => p.CompromissoId == l.c.Id).Select(p => p.Email!)]))];
    }

    public async Task<IReadOnlyList<CompromissoDetalhe>> ListarAsync(UsuarioAtual u, FiltroAgenda f)
    {
        u.Exigir(P.AgendaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        var q = Visiveis(db, u);
        if (f.De is { } de) q = q.Where(c => c.FimEm >= de);
        if (f.Ate is { } ate) q = q.Where(c => c.InicioEm <= ate);
        if (f.ImovelId is { } im) q = q.Where(c => c.ImovelId == im);
        if (f.ResponsavelId is { } r) q = q.Where(c => c.ResponsavelId == r);
        if (f.Status is { } s) q = q.Where(c => c.Status == s);
        return await Detalhar(db, q);
    }

    public async Task<IReadOnlyList<CompromissoDetalhe>> DoImovelAsync(UsuarioAtual u, Guid imovelId)
    {
        u.Exigir(P.AgendaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        return await Detalhar(db, Visiveis(db, u).Where(c => c.ImovelId == imovelId));
    }

    public async Task<CompromissoDetalhe> ObterAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.AgendaVer);
        await using var db = await fabrica.CreateDbContextAsync();
        return (await Detalhar(db, Visiveis(db, u).Where(c => c.Id == id))).SingleOrDefault()
            ?? throw new NaoEncontradoException("Compromisso não encontrado.");
    }

    private static async Task GravarParticipantes(InovabensDb db, Guid compromissoId, IReadOnlyList<string> emails)
    {
        await db.CompromissoParticipantes.Where(p => p.CompromissoId == compromissoId).ExecuteDeleteAsync();
        db.CompromissoParticipantes.AddRange(emails.Select(e => new CompromissoParticipante { CompromissoId = compromissoId, Email = e }));
    }

    private static DateTime Utc(DateTime br) => DatasBR.ParaUtc(br);

    public async Task<Guid> CriarAsync(UsuarioAtual u, Guid imovelId, CompromissoForm form)
    {
        u.Exigir(P.AgendaCriar);
        Validacao.Validar(form);
        await using var db = await fabrica.CreateDbContextAsync();
        await Acesso.ImovelComAcesso(db, u, imovelId, rastrear: false);
        var c = new Compromisso
        {
            Id = Guid.NewGuid(), ImovelId = imovelId, Tipo = form.Tipo, Titulo = form.Titulo.Trim(),
            Descricao = Texto.Limpo(form.Descricao), Local = Texto.Limpo(form.Local),
            InicioEm = Utc(form.Inicio!.Value), FimEm = Utc(form.Fim!.Value), LeadId = form.LeadId,
            ResponsavelId = Acesso.ResponsavelAoCriar(u, form.ResponsavelId),
        };
        await using var tx = await db.Database.BeginTransactionAsync();
        db.Compromissos.Add(c);
        await GravarParticipantes(db, c.Id, form.Emails);
        audit.Registrar(db, u.Id, "compromissos", c.Id, "CRIAR", depois: c);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        google.Empurrar(c.Id);
        return c.Id;
    }

    public async Task AtualizarAsync(UsuarioAtual u, Guid id, CompromissoForm form)
    {
        u.Exigir(P.AgendaEditar);
        Validacao.Validar(form);
        await Alterar(u, id, "ATUALIZAR", async (db, c) =>
        {
            c.Tipo = form.Tipo;
            c.Titulo = form.Titulo.Trim();
            c.Descricao = Texto.Limpo(form.Descricao);
            c.Local = Texto.Limpo(form.Local);
            c.InicioEm = Utc(form.Inicio!.Value);
            c.FimEm = Utc(form.Fim!.Value);
            c.LeadId = form.LeadId;
            if (Acesso.NovoResponsavel(u, form.ResponsavelId) is { } r) c.ResponsavelId = r;
            await GravarParticipantes(db, id, form.Emails);
        });
    }

    public Task MudarStatusAsync(UsuarioAtual u, Guid id, StatusCompromisso status)
    {
        u.Exigir(P.AgendaEditar);
        return Alterar(u, id, "ATUALIZAR", (_, c) =>
        {
            c.Status = status;
            c.ConcluidoEm = status == StatusCompromisso.CONCLUIDO ? DateTime.UtcNow : null;
            return Task.CompletedTask;
        });
    }

    public Task ConcluirAsync(UsuarioAtual u, Guid id) => MudarStatusAsync(u, id, StatusCompromisso.CONCLUIDO);

    /// <summary>Novo horário a partir de um alerta de "não cumprido": volta a PENDENTE e o alerta é encerrado.</summary>
    public async Task ReagendarPorAlertaAsync(UsuarioAtual u, Guid alertaId, DateTime inicioBR, DateTime fimBR)
    {
        u.Exigir(P.AgendaEditar);
        if (fimBR < inicioBR) throw new RegraNegocioException("Fim", "O término deve ser depois do início");
        Guid compromissoId;
        await using (var db = await fabrica.CreateDbContextAsync())
            compromissoId = await db.Alertas.Where(a => a.Id == alertaId && a.UsuarioId == u.Id && a.CompromissoId != null)
                .Select(a => a.CompromissoId!.Value).SingleOrDefaultAsync();
        if (compromissoId == Guid.Empty) throw new NaoEncontradoException("Alerta de compromisso não encontrado.");
        await Alterar(u, compromissoId, "ATUALIZAR", (_, c) =>
        {
            c.InicioEm = Utc(inicioBR);
            c.FimEm = Utc(fimBR);
            c.Status = StatusCompromisso.PENDENTE;
            c.ConcluidoEm = null;
            return Task.CompletedTask;
        });
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.AgendaExcluir);
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Rastreado(db, u, id);
        var antes = c.Copia();
        c.ExcluidoEm = DateTime.UtcNow;
        audit.Registrar(db, u.Id, "compromissos", id, "EXCLUIR", antes);
        await db.SaveChangesAsync();
        await AlertasService.ResolverDoCompromisso(db, id);
        google.RemoverDoGoogle(antes.ResponsavelId, antes.GoogleEventId);
    }

    private static async Task<Compromisso> Rastreado(InovabensDb db, UsuarioAtual u, Guid id)
    {
        var visivel = await Visiveis(db, u).AnyAsync(c => c.Id == id);
        if (!visivel) throw new NaoEncontradoException("Compromisso não encontrado.");
        return await db.Compromissos.SingleAsync(c => c.Id == id);
    }

    private async Task Alterar(UsuarioAtual u, Guid id, string acao, Func<InovabensDb, Compromisso, Task> mudar)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var c = await Rastreado(db, u, id);
        var antes = c.Copia();
        await using var tx = await db.Database.BeginTransactionAsync();
        await mudar(db, c);
        audit.Registrar(db, u.Id, "compromissos", id, acao, antes, c);
        await db.SaveChangesAsync();
        // concluir, cancelar ou reagendar para o futuro encerra o alerta de "não cumprido"
        if (c.Status != StatusCompromisso.PENDENTE || c.FimEm > DateTime.UtcNow) await AlertasService.ResolverDoCompromisso(db, id);
        await tx.CommitAsync();
        google.Empurrar(id);
    }
}
