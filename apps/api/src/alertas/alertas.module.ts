import { Controller, Get, HttpCode, Inject, Injectable, Logger, Module, NotFoundException, Param, ParseUUIDPipe, Patch, Query } from '@nestjs/common';
import { Cron } from '@nestjs/schedule';
import { and, desc, eq, isNull, sql } from 'drizzle-orm';
import { z } from 'zod';
import type { AuthUser } from '../common/auth.types';
import { CurrentUser } from '../common/decorators';
import { hojeBR } from '../common/datas';
import { ZodPipe } from '../common/zod.pipe';
import { env } from '../config/env';
import { DB, Database } from '../db/db.module';
import { alertas, compromissos, despesa_lancamentos, despesas } from '../db/schema';
import { FinanceiroModule } from '../financeiro/financeiro.module';
import { DespesasService } from '../financeiro/despesas.service';

const query = z.object({ status: z.enum(['aberto', 'todos']).default('aberto') });

@Injectable()
export class AlertasService {
  private readonly log = new Logger('Alertas');
  constructor(@Inject(DB) private readonly db: Database, private readonly despesas: DespesasService) {}

  /* --------------------------- Consulta --------------------------- */
  async listar(user: AuthUser, status: 'aberto' | 'todos') {
    const rows = await this.db.select({ a: alertas, c: { id: compromissos.id, titulo: compromissos.titulo, fim_em: compromissos.fim_em, imovel_id: compromissos.imovel_id }, d_imovel: despesas.imovel_id })
      .from(alertas)
      .leftJoin(compromissos, eq(compromissos.id, alertas.compromisso_id))
      .leftJoin(despesa_lancamentos, eq(despesa_lancamentos.id, alertas.lancamento_id))
      .leftJoin(despesas, eq(despesas.id, despesa_lancamentos.despesa_id))
      .where(and(eq(alertas.usuario_id, user.id), status === 'aberto' ? isNull(alertas.resolvido_em) : undefined))
      .orderBy(desc(alertas.gerado_em)).limit(200);
    return rows.map((r) => ({ ...r.a, compromisso: r.c?.id ? r.c : null, imovel_id: r.c?.imovel_id ?? r.d_imovel ?? null }));
  }
  async contagem(user: AuthUser) {
    const [{ n }] = await this.db.select({ n: sql<number>`count(*)::int` }).from(alertas).where(and(eq(alertas.usuario_id, user.id), isNull(alertas.resolvido_em)));
    return { abertos: n };
  }
  async marcarLido(user: AuthUser, id: string) {
    const [a] = await this.db.update(alertas).set({ lido_em: new Date() }).where(and(eq(alertas.id, id), eq(alertas.usuario_id, user.id))).returning();
    if (!a) throw new NotFoundException('Alerta não encontrado');
    return a;
  }

  /** Chamado quando um compromisso é concluído/cancelado/reagendado. */
  async resolverDoCompromisso(compromissoId: string) {
    await this.db.update(alertas).set({ resolvido_em: new Date() }).where(and(eq(alertas.compromisso_id, compromissoId), isNull(alertas.resolvido_em)));
  }

  /* ------------------------------ Jobs ------------------------------ */
  /**
   * REGRA: compromisso PENDENTE cujo fim_em já passou gera 1 alerta aberto (responsável).
   * Idempotente (índice único parcial). Resolve alertas cujo compromisso foi concluído/cancelado/reagendado.
   */
  async verificarCompromissos() {
    const novos = await this.db.execute(sql`
      insert into alertas (tipo, usuario_id, compromisso_id, mensagem)
      select 'COMPROMISSO_NAO_CUMPRIDO'::tipo_alerta, c.responsavel_id, c.id,
             'Compromisso não cumprido: ' || c.titulo || ' (' || i.codigo || ') — terminou em '
             || to_char(c.fim_em at time zone 'America/Sao_Paulo', 'DD/MM/YYYY HH24:MI')
        from compromissos c join imoveis i on i.id = c.imovel_id
       where c.status = 'PENDENTE' and c.fim_em < now() and c.excluido_em is null
      on conflict do nothing
      returning id`);
    const resolvidos = await this.db.execute(sql`
      update alertas a set resolvido_em = now()
       where a.resolvido_em is null and a.tipo = 'COMPROMISSO_NAO_CUMPRIDO'
         and exists (select 1 from compromissos c where c.id = a.compromisso_id
                      and (c.status <> 'PENDENTE' or c.fim_em >= now() or c.excluido_em is not null))
      returning a.id`);
    return { criados: novos.rows.length, resolvidos: resolvidos.rows.length };
  }

  /** Despesas vencidas sem pagamento (a marcar ou NAO_PAGO) alertam quem tem acesso ao financeiro. */
  async verificarDespesas() {
    const hoje = hojeBR();
    const novos = await this.db.execute(sql`
      insert into alertas (tipo, usuario_id, lancamento_id, mensagem)
      select 'DESPESA_NAO_PAGA'::tipo_alerta, u.id, l.id,
             'Despesa vencida em ' || to_char(l.vencimento, 'DD/MM/YYYY') || ': ' || d.descricao || ' (' || i.codigo || ')'
        from despesa_lancamentos l
        join despesas d on d.id = l.despesa_id join imoveis i on i.id = d.imovel_id
        cross join (select distinct u2.id from usuarios u2
                      join perfil_permissoes pp on pp.perfil_id = u2.perfil_id join permissoes m on m.id = pp.permissao_id
                     where m.codigo = 'financeiro:ver' and u2.ativo and u2.excluido_em is null) u
       where (l.status is null or l.status = 'NAO_PAGO') and l.vencimento < ${hoje}::date
         and d.excluido_em is null and i.excluido_em is null
      on conflict do nothing
      returning id`);
    const resolvidos = await this.db.execute(sql`
      update alertas a set resolvido_em = now()
       where a.resolvido_em is null and a.tipo = 'DESPESA_NAO_PAGA'
         and exists (select 1 from despesa_lancamentos l join despesas d on d.id = l.despesa_id
                      where l.id = a.lancamento_id and (l.status in ('PAGO','NAO_SE_APLICA') or d.excluido_em is not null))
      returning a.id`);
    return { criados: novos.rows.length, resolvidos: resolvidos.rows.length };
  }

  @Cron('*/5 * * * *', { timeZone: 'America/Sao_Paulo' })
  async jobCompromissos() {
    if (env().DISABLE_JOBS) return;
    try { const r = await this.verificarCompromissos(); if (r.criados || r.resolvidos) this.log.log(`Compromissos: ${JSON.stringify(r)}`); } catch (e) { this.log.error(String(e)); }
  }

  /** Todo dia 06:00 (Brasília): gera os lançamentos do mês e alerta despesas vencidas. */
  @Cron('0 6 * * *', { timeZone: 'America/Sao_Paulo' })
  async jobDiario() {
    if (env().DISABLE_JOBS) return;
    try {
      const gerados = await this.despesas.gerarCompetencia(`${hojeBR().slice(0, 7)}-01`);
      this.log.log(`Lançamentos gerados: ${gerados}; despesas: ${JSON.stringify(await this.verificarDespesas())}`);
    } catch (e) { this.log.error(String(e)); }
  }
}

@Controller('alertas')
class AlertasController {
  constructor(private readonly s: AlertasService) {}
  @Get() listar(@CurrentUser() u: AuthUser, @Query(new ZodPipe(query)) q: z.infer<typeof query>) { return this.s.listar(u, q.status); }
  @Get('contagem') contagem(@CurrentUser() u: AuthUser) { return this.s.contagem(u); }
  @Patch(':id/lido') @HttpCode(200) lido(@CurrentUser() u: AuthUser, @Param('id', ParseUUIDPipe) id: string) { return this.s.marcarLido(u, id); }
}

@Module({ imports: [FinanceiroModule], controllers: [AlertasController], providers: [AlertasService], exports: [AlertasService] })
export class AlertasModule {}
