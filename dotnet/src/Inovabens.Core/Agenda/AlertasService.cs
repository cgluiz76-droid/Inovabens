using Inovabens.Core.Comum;
using Inovabens.Core.Dados;
using Inovabens.Core.Dominio;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Inovabens.Core.Agenda;

public sealed record AlertaItem(Alerta Dados, Guid? CompromissoId, string? CompromissoTitulo, Guid? ImovelId);

public sealed record ResultadoVerificacao(int Criados, int Resolvidos);

/// <summary>Alertas do usuário e as verificações periódicas (port de alertas.module.ts).</summary>
public sealed class AlertasService(IDbContextFactory<InovabensDb> fabrica)
{
    public async Task<IReadOnlyList<AlertaItem>> ListarAsync(UsuarioAtual u, bool incluirResolvidos)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var q = db.Alertas.AsNoTracking().Where(a => a.UsuarioId == u.Id);
        if (!incluirResolvidos) q = q.Where(a => a.ResolvidoEm == null);
        return await (
            from a in q
            join c in db.Compromissos on a.CompromissoId equals c.Id into cs
            from c in cs.DefaultIfEmpty()
            join l in db.DespesaLancamentos on a.LancamentoId equals l.Id into ls
            from l in ls.DefaultIfEmpty()
            orderby a.GeradoEm descending
            select new AlertaItem(a, c == null ? null : c.Id, c == null ? null : c.Titulo,
                c != null ? c.ImovelId : l != null ? l.Despesa!.ImovelId : null)
        ).Take(200).ToListAsync();
    }

    public async Task<int> ContagemAsync(UsuarioAtual u)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        return await db.Alertas.CountAsync(a => a.UsuarioId == u.Id && a.ResolvidoEm == null);
    }

    public async Task MarcarLidoAsync(UsuarioAtual u, Guid id)
    {
        await using var db = await fabrica.CreateDbContextAsync();
        var n = await db.Alertas.Where(a => a.Id == id && a.UsuarioId == u.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LidoEm, DateTime.UtcNow));
        if (n == 0) throw new NaoEncontradoException("Alerta não encontrado.");
    }

    /// <summary>Compromisso concluído, cancelado, excluído ou reagendado para o futuro encerra o alerta de "não cumprido".</summary>
    internal static Task ResolverDoCompromisso(InovabensDb db, Guid compromissoId) =>
        db.Alertas.Where(a => a.CompromissoId == compromissoId && a.ResolvidoEm == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvidoEm, DateTime.UtcNow));

    /// <summary>
    /// REGRA: compromisso PENDENTE cujo fim já passou gera 1 alerta aberto para o responsável (idempotente:
    /// índice único parcial). Resolve os alertas de compromissos que deixaram de estar vencidos.
    /// </summary>
    public async Task<ResultadoVerificacao> VerificarCompromissosAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var criados = await db.Database.ExecuteSqlRawAsync("""
            insert into alertas (tipo, usuario_id, compromisso_id, mensagem)
            select 'COMPROMISSO_NAO_CUMPRIDO'::tipo_alerta, c.responsavel_id, c.id,
                   'Compromisso não cumprido: ' || c.titulo || ' (' || i.codigo || ') — terminou em '
                   || to_char(c.fim_em at time zone 'America/Sao_Paulo', 'DD/MM/YYYY HH24:MI')
              from compromissos c join imoveis i on i.id = c.imovel_id
             where c.status = 'PENDENTE' and c.fim_em < now() and c.excluido_em is null
            on conflict do nothing
            """, ct);
        var resolvidos = await db.Database.ExecuteSqlRawAsync("""
            update alertas a set resolvido_em = now()
             where a.resolvido_em is null and a.tipo = 'COMPROMISSO_NAO_CUMPRIDO'
               and exists (select 1 from compromissos c where c.id = a.compromisso_id
                            and (c.status <> 'PENDENTE' or c.fim_em >= now() or c.excluido_em is not null))
            """, ct);
        return new ResultadoVerificacao(criados, resolvidos);
    }

    /// <summary>Despesas vencidas sem pagamento (a marcar ou NAO_PAGO) alertam cada usuário com financeiro:ver.</summary>
    public async Task<ResultadoVerificacao> VerificarDespesasAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var criados = await db.Database.ExecuteSqlRawAsync("""
            insert into alertas (tipo, usuario_id, lancamento_id, mensagem)
            select 'DESPESA_NAO_PAGA'::tipo_alerta, u.id, l.id,
                   'Despesa vencida em ' || to_char(l.vencimento, 'DD/MM/YYYY') || ': ' || d.descricao || ' (' || i.codigo || ')'
              from despesa_lancamentos l
              join despesas d on d.id = l.despesa_id join imoveis i on i.id = d.imovel_id
              cross join (select distinct u2.id from usuarios u2
                            join perfil_permissoes pp on pp.perfil_id = u2.perfil_id join permissoes m on m.id = pp.permissao_id
                           where m.codigo = 'financeiro:ver' and u2.ativo and u2.excluido_em is null) u
             where (l.status is null or l.status = 'NAO_PAGO') and l.vencimento < @hoje
               and d.excluido_em is null and i.excluido_em is null
            on conflict do nothing
            """, [new NpgsqlParameter("hoje", NpgsqlDbType.Date) { Value = DatasBR.Hoje() }], ct);
        var resolvidos = await db.Database.ExecuteSqlRawAsync("""
            update alertas a set resolvido_em = now()
             where a.resolvido_em is null and a.tipo = 'DESPESA_NAO_PAGA'
               and exists (select 1 from despesa_lancamentos l join despesas d on d.id = l.despesa_id
                            where l.id = a.lancamento_id and (l.status in ('PAGO','NAO_SE_APLICA') or d.excluido_em is not null))
            """, ct);
        return new ResultadoVerificacao(criados, resolvidos);
    }
}
