import { Inject, Injectable, NotFoundException, UnprocessableEntityException } from '@nestjs/common';
import { and, asc, desc, eq, gte, isNull, lte, sql } from 'drizzle-orm';
import { z } from 'zod';
import type { StatusLancamento } from '@inovabens/shared';
import { atualizarStatusLancamentoSchema } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { hojeBR } from '../common/datas';
import { assertAcessoImovel } from '../common/escopo';
import { DB, Database } from '../db/db.module';
import { alertas, despesa_categorias, despesa_lancamentos, despesas } from '../db/schema';
import { categoriaPatchSchema, categoriaSchema, despesaAtualizarSchema, despesaCriarSchema, valorLancamento } from './financeiro.schemas';

@Injectable()
export class DespesasService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  /* ---------------------------- Categorias ---------------------------- */
  categorias(incluirInativas = false) {
    return this.db.select().from(despesa_categorias).where(incluirInativas ? undefined : eq(despesa_categorias.ativo, true)).orderBy(asc(despesa_categorias.nome));
  }
  async criarCategoria(b: z.infer<typeof categoriaSchema>) {
    const [r] = await this.db.insert(despesa_categorias).values(b).returning();
    return r;
  }
  async atualizarCategoria(id: string, b: z.infer<typeof categoriaPatchSchema>) {
    const [r] = await this.db.update(despesa_categorias).set(b).where(eq(despesa_categorias.id, id)).returning();
    if (!r) throw new NotFoundException('Categoria não encontrada');
    return r;
  }

  /* ----------------------------- Despesas ----------------------------- */
  async listar(user: AuthUser, imovelId: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const rows = await this.db.select({ d: despesas, categoria: despesa_categorias.nome }).from(despesas)
      .innerJoin(despesa_categorias, eq(despesa_categorias.id, despesas.categoria_id))
      .where(and(eq(despesas.imovel_id, imovelId), isNull(despesas.excluido_em))).orderBy(asc(despesa_categorias.nome), asc(despesas.descricao));
    return rows.map((r) => ({ ...r.d, categoria: r.categoria }));
  }
  async criar(user: AuthUser, imovelId: string, b: z.infer<typeof despesaCriarSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const [r] = await this.db.insert(despesas).values({ ...b, imovel_id: imovelId }).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'despesas', entidade_id: r.id, acao: 'CRIAR', depois: r, ip });
    return r;
  }
  private async doUsuario(user: AuthUser, id: string) {
    const [d] = await this.db.select().from(despesas).where(and(eq(despesas.id, id), isNull(despesas.excluido_em)));
    if (!d) throw new NotFoundException('Despesa não encontrada');
    await assertAcessoImovel(this.db, user, d.imovel_id);
    return d;
  }
  async atualizar(user: AuthUser, id: string, b: z.infer<typeof despesaAtualizarSchema>, ip: string) {
    const antes = await this.doUsuario(user, id);
    const [r] = await this.db.update(despesas).set(b).where(eq(despesas.id, id)).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'despesas', entidade_id: id, acao: 'ATUALIZAR', antes, depois: r, ip });
    return r;
  }
  async excluir(user: AuthUser, id: string, ip: string) {
    const antes = await this.doUsuario(user, id);
    await this.db.update(despesas).set({ excluido_em: new Date(), ativa: false }).where(eq(despesas.id, id));
    await this.audit.log({ usuario_id: user.id, entidade: 'despesas', entidade_id: id, acao: 'EXCLUIR', antes, ip });
  }

  /* ------------------- Competências (checklist mensal) ------------------- */
  /**
   * Gera (idempotente) os lançamentos do mês para as despesas ativas: MENSAL todo mês; ANUAL no mês de `inicio_em`;
   * UNICA no mês de `inicio_em`. Status nasce em branco ("a marcar"). `mes` = AAAA-MM-01.
   */
  async gerarCompetencia(mes: string, imovelId?: string): Promise<number> {
    const r = await this.db.execute(sql`
      insert into despesa_lancamentos (despesa_id, competencia, vencimento, valor)
      select d.id, ${mes}::date,
             make_date(extract(year from ${mes}::date)::int, extract(month from ${mes}::date)::int,
               least(coalesce(d.dia_vencimento, extract(day from d.inicio_em)::int),
                     extract(day from (date_trunc('month', ${mes}::date) + interval '1 month' - interval '1 day'))::int)),
             d.valor_previsto
        from despesas d
       where d.ativa and d.excluido_em is null
         and (${imovelId ?? null}::uuid is null or d.imovel_id = ${imovelId ?? null}::uuid)
         and d.inicio_em <= (date_trunc('month', ${mes}::date) + interval '1 month' - interval '1 day')::date
         and (d.fim_em is null or d.fim_em >= ${mes}::date)
         and (d.recorrencia = 'MENSAL'
              or (d.recorrencia = 'ANUAL' and extract(month from d.inicio_em) = extract(month from ${mes}::date))
              or (d.recorrencia = 'UNICA' and date_trunc('month', d.inicio_em) = date_trunc('month', ${mes}::date)))
      on conflict (despesa_id, competencia) do nothing
      returning id`);
    return r.rows.length;
  }

  async checklist(user: AuthUser, imovelId: string, mes: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    await this.gerarCompetencia(`${mes}-01`, imovelId);
    const rows = await this.db.select({ l: despesa_lancamentos, d: despesas, categoria: despesa_categorias.nome })
      .from(despesa_lancamentos)
      .innerJoin(despesas, eq(despesas.id, despesa_lancamentos.despesa_id))
      .innerJoin(despesa_categorias, eq(despesa_categorias.id, despesas.categoria_id))
      .where(and(eq(despesas.imovel_id, imovelId), isNull(despesas.excluido_em), eq(despesa_lancamentos.competencia, `${mes}-01`)))
      .orderBy(asc(despesa_categorias.nome), asc(despesas.descricao));
    const itens = rows.map(({ l, d, categoria }) => ({
      despesa_id: d.id, lancamento_id: l.id, descricao: d.descricao, categoria, recorrencia: d.recorrencia,
      vencimento: l.vencimento, valor: l.valor, status: l.status, data_pagamento: l.data_pagamento, justificativa: l.justificativa,
    }));
    const conta = (s: StatusLancamento | null) => itens.filter((i) => i.status === s).length;
    return { mes, resumo: { pago: conta('PAGO'), nao_pago: conta('NAO_PAGO'), nao_se_aplica: conta('NAO_SE_APLICA'), a_marcar: conta(null) }, itens };
  }

  async historico(user: AuthUser, imovelId: string, de?: string, ate?: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const rows = await this.db.select({ l: despesa_lancamentos, d: despesas, categoria: despesa_categorias.nome })
      .from(despesa_lancamentos)
      .innerJoin(despesas, eq(despesas.id, despesa_lancamentos.despesa_id))
      .innerJoin(despesa_categorias, eq(despesa_categorias.id, despesas.categoria_id))
      .where(and(eq(despesas.imovel_id, imovelId), isNull(despesas.excluido_em),
        de ? gte(despesa_lancamentos.competencia, `${de}-01`) : undefined, ate ? lte(despesa_lancamentos.competencia, `${ate}-01`) : undefined))
      .orderBy(desc(despesa_lancamentos.competencia), asc(despesa_categorias.nome));
    return rows.map(({ l, d, categoria }) => ({ ...l, descricao: d.descricao, categoria }));
  }

  /**
   * REGRA DE NEGÓCIO: status NAO_PAGO exige justificativa (validada aqui, no schema compartilhado,
   * e também pela constraint ck_justificativa_nao_pago no banco).
   */
  async atualizarStatus(user: AuthUser, lancamentoId: string, body: z.infer<typeof atualizarStatusLancamentoSchema>, ip: string) {
    const [x] = await this.db.select({ l: despesa_lancamentos, imovel_id: despesas.imovel_id }).from(despesa_lancamentos)
      .innerJoin(despesas, eq(despesas.id, despesa_lancamentos.despesa_id)).where(eq(despesa_lancamentos.id, lancamentoId));
    if (!x) throw new NotFoundException('Lançamento não encontrado');
    await assertAcessoImovel(this.db, user, x.imovel_id);
    let valor: string | undefined;
    if (body.valor !== undefined) {
      const v = valorLancamento.safeParse(body.valor);
      if (!v.success) throw new UnprocessableEntityException({ errors: { valor: ['Valor inválido'] } });
      valor = v.data;
    }
    const comNota = body.status === 'NAO_PAGO' || body.status === 'NAO_SE_APLICA';
    const [depois] = await this.db.update(despesa_lancamentos).set({
      status: body.status,
      justificativa: comNota ? (body.justificativa || null) : null,
      data_pagamento: body.status === 'PAGO' ? (body.data_pagamento ?? hojeBR()) : null,
      valor,
      atualizado_por: user.id,
    }).where(eq(despesa_lancamentos.id, lancamentoId)).returning();
    if (body.status === 'PAGO' || body.status === 'NAO_SE_APLICA') {
      await this.db.update(alertas).set({ resolvido_em: new Date() }).where(and(eq(alertas.lancamento_id, lancamentoId), isNull(alertas.resolvido_em)));
    }
    await this.audit.log({ usuario_id: user.id, entidade: 'despesa_lancamentos', entidade_id: lancamentoId, acao: 'ATUALIZAR', antes: x.l, depois, ip });
    return depois;
  }
}
