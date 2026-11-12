using System.ComponentModel.DataAnnotations;
using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Inovabens.Core.Servicos;
using Microsoft.EntityFrameworkCore;
using P = Inovabens.Core.Dominio.Permissoes;

namespace Inovabens.Core.Crm;

/// <summary>Meta e realizado de um usuário no mês.</summary>
public sealed record LinhaMeta(Guid UsuarioId, string Nome, Guid? MetaId, int MetaAtendimentos, int MetaPropostas, int MetaFechamentos,
    int Atendimentos, int Propostas, int Fechamentos)
{
    public bool TemMeta => MetaId is not null;

    /// <summary>Percentual atingido (0–999); sem meta, null.</summary>
    public static int? Pct(int feito, int meta) => meta > 0 ? Math.Min(999, (int)Math.Round(feito * 100m / meta)) : null;
}

public sealed class MetaForm
{
    [Required(ErrorMessage = "Escolha o usuário.")] public Guid? UsuarioId { get; set; }
    public DateOnly Mes { get; set; } = MetasService.MesDe(DatasBR.Hoje());
    [Range(0, 100_000, ErrorMessage = "De 0 a 100000.")] public int Atendimentos { get; set; }
    [Range(0, 100_000, ErrorMessage = "De 0 a 100000.")] public int Propostas { get; set; }
    [Range(0, 100_000, ErrorMessage = "De 0 a 100000.")] public int Fechamentos { get; set; }
}

/// <summary>
/// Metas mensais por usuário e o realizado no mês (horário de Brasília):
/// atendimentos = leads distintos com ação registrada pelo usuário; propostas = propostas registradas pelo usuário;
/// fechamentos = leads do usuário (responsável) que entraram numa etapa de ganho.
/// Perfil com escopo próprio vê só a própria linha; definir metas exige admin:config.
/// </summary>
public sealed class MetasService(IDbContextFactory<InovabensDb> fabrica, AuditService audit)
{
    public static DateOnly MesDe(DateOnly d) => new(d.Year, d.Month, 1);

    public async Task<IReadOnlyList<LinhaMeta>> PainelAsync(UsuarioAtual u, DateOnly mes)
    {
        u.Exigir(P.CrmVer);
        mes = MesDe(mes);
        var ini = DatasBR.ParaUtc(mes.ToDateTime(TimeOnly.MinValue));
        var fim = DatasBR.ParaUtc(mes.AddMonths(1).ToDateTime(TimeOnly.MinValue));
        await using var db = await fabrica.CreateDbContextAsync();

        var atend = await db.LeadInteracoes.AsNoTracking()
            .Where(i => i.UsuarioId != null && i.CriadoEm >= ini && i.CriadoEm < fim)
            .GroupBy(i => i.UsuarioId!.Value).Select(g => new { g.Key, N = g.Select(i => i.LeadId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var props = await db.Propostas.AsNoTracking()
            .Where(p => p.CriadoPor != null && p.CriadoEm >= ini && p.CriadoEm < fim)
            .GroupBy(p => p.CriadoPor!.Value).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
        var fech = (await (
                from h in db.LeadHistoricoEstagios
                join e in db.FunilEstagios on h.EstagioNovoId equals e.Id
                join l in db.Leads on h.LeadId equals l.Id
                where e.Tipo == TipoEstagio.GANHO && h.CriadoEm >= ini && h.CriadoEm < fim && l.ExcluidoEm == null
                select new { Quem = l.ResponsavelId ?? h.UsuarioId, h.LeadId })
            .ToListAsync())
            .Where(x => x.Quem is not null).GroupBy(x => x.Quem!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.LeadId).Distinct().Count());
        var metas = await db.Metas.AsNoTracking().Where(m => m.Mes == mes).ToDictionaryAsync(m => m.UsuarioId);

        var ids = metas.Keys.Concat(atend.Keys).Concat(props.Keys).Concat(fech.Keys).ToHashSet();
        if (u.EscopoProprio) ids = [u.Id];
        var nomes = await db.Usuarios.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Nome);
        return [.. ids.Where(nomes.ContainsKey).Select(id =>
            {
                metas.TryGetValue(id, out var m);
                return new LinhaMeta(id, nomes[id], m?.Id, m?.Atendimentos ?? 0, m?.Propostas ?? 0, m?.Fechamentos ?? 0,
                    atend.GetValueOrDefault(id), props.GetValueOrDefault(id), fech.GetValueOrDefault(id));
            })
            .OrderByDescending(l => l.TemMeta).ThenBy(l => l.Nome)];
    }

    /// <summary>Cria ou altera a meta do usuário no mês.</summary>
    public async Task SalvarAsync(UsuarioAtual u, MetaForm form)
    {
        u.Exigir(P.AdminConfig);
        Validacao.Validar(form);
        var mes = MesDe(form.Mes);
        await using var db = await fabrica.CreateDbContextAsync();
        if (!await db.Usuarios.AnyAsync(x => x.Id == form.UsuarioId && x.ExcluidoEm == null))
            throw new NaoEncontradoException("Usuário não encontrado.");
        var m = await db.Metas.SingleOrDefaultAsync(x => x.UsuarioId == form.UsuarioId && x.Mes == mes);
        var antes = m is null ? null : new { m.Atendimentos, m.Propostas, m.Fechamentos };
        if (m is null)
        {
            m = new Meta { Id = Guid.NewGuid(), UsuarioId = form.UsuarioId!.Value, Mes = mes };
            db.Metas.Add(m);
        }
        (m.Atendimentos, m.Propostas, m.Fechamentos, m.AtualizadoEm) = (form.Atendimentos, form.Propostas, form.Fechamentos, DateTime.UtcNow);
        audit.Registrar(db, u.Id, "metas", m.Id, antes is null ? "CRIAR" : "ATUALIZAR", antes, new { m.UsuarioId, m.Mes, m.Atendimentos, m.Propostas, m.Fechamentos });
        await db.SaveChangesAsync();
    }

    public async Task ExcluirAsync(UsuarioAtual u, Guid id)
    {
        u.Exigir(P.AdminConfig);
        await using var db = await fabrica.CreateDbContextAsync();
        var m = await db.Metas.SingleOrDefaultAsync(x => x.Id == id) ?? throw new NaoEncontradoException("Meta não encontrada.");
        db.Metas.Remove(m);
        audit.Registrar(db, u.Id, "metas", id, "EXCLUIR", new { m.UsuarioId, m.Mes, m.Atendimentos, m.Propostas, m.Fechamentos });
        await db.SaveChangesAsync();
    }
}
