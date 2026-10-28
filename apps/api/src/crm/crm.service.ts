import { ConflictException, Inject, Injectable, NotFoundException, UnprocessableEntityException } from '@nestjs/common';
import { and, asc, desc, eq, ilike, isNull, or, sql } from 'drizzle-orm';
import { z } from 'zod';
import { CANAL_INTERACAO, FINALIDADE_LEAD, FUNIL, TIPO_ESTAGIO } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { offset, paginacaoSchema, paginado } from '../common/pagination';
import { blank, dinheiroPositivo, likeLiteral, uuid } from '../common/schemas';
import { DB, Database } from '../db/db.module';
import {
  funil_estagios, imoveis, lead_historico_estagio, lead_imoveis_interesse, lead_interacoes, leads, pessoa_papeis, pessoas, usuarios,
} from '../db/schema';

type Funil = (typeof FUNIL)[number];
export const funilDaFinalidade = (f: (typeof FINALIDADE_LEAD)[number]): Funil => (f === 'COMPRA' ? 'VENDA' : 'LOCACAO');

const leadBase = z.object({
  finalidade: z.enum(FINALIDADE_LEAD).default('COMPRA'),
  origem: blank(z.string().trim().max(80)),
  estagio_id: blank(uuid),
  responsavel_id: blank(uuid),
  orcamento_min: blank(dinheiroPositivo), orcamento_max: blank(dinheiroPositivo),
  proxima_acao_em: blank(z.coerce.date()),
});
export const leadCriar = leadBase.extend({
  pessoa_id: blank(uuid),
  pessoa: z.object({ nome_razao: z.string().trim().min(2).max(200), telefone: blank(z.string().trim().max(30)), email: blank(z.string().trim().toLowerCase().email()) }).optional(),
}).refine((v) => v.pessoa_id || v.pessoa, { path: ['pessoa_id'], message: 'Informe a pessoa (pessoa_id) ou os dados do novo contato (pessoa)' });
export const leadAtualizar = leadBase.partial();
export const moverSchema = z.object({ estagio_id: uuid, ordem: z.number().int().min(0).optional(), motivo_perda: blank(z.string().trim().max(500)) });
export const interacaoSchema = z.object({ canal: z.enum(CANAL_INTERACAO), resumo: z.string().trim().min(2) });
export const interesseSchema = z.array(uuid).max(100);
export const estagioCriar = z.object({
  funil: z.enum(FUNIL).default('VENDA'), nome: z.string().trim().min(2).max(60), ordem: z.number().int().min(0).default(0),
  cor: z.string().regex(/^#[0-9a-fA-F]{6}$/).default('#64748b'), tipo: z.enum(TIPO_ESTAGIO).default('ABERTO'), ativo: z.boolean().default(true),
});
export const estagioAtualizar = estagioCriar.partial();
export const ordemSchema = z.array(z.object({ id: uuid, ordem: z.number().int().min(0) }));
export const listaLeads = paginacaoSchema.extend({
  q: z.string().trim().max(100).optional(), estagio_id: uuid.optional(), finalidade: z.enum(FINALIDADE_LEAD).optional(),
});
export const pipelineQuery = z.object({ funil: z.enum(FUNIL).default('VENDA') });
export const estagiosQuery = z.object({ funil: z.enum(FUNIL).optional() });

@Injectable()
export class CrmService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  private escopo = (user: AuthUser) => (user.escopo_proprio ? eq(leads.responsavel_id, user.id) : undefined);

  /* ----------------------------- Pipeline ----------------------------- */
  async pipeline(user: AuthUser, funil: Funil) {
    const est = await this.db.select().from(funil_estagios).where(and(eq(funil_estagios.funil, funil), eq(funil_estagios.ativo, true))).orderBy(asc(funil_estagios.ordem));
    const rows = await this.db.select({
      l: leads, p: { id: pessoas.id, nome_razao: pessoas.nome_razao, telefone: pessoas.telefone, email: pessoas.email }, r: { id: usuarios.id, nome: usuarios.nome },
    }).from(leads).innerJoin(pessoas, eq(pessoas.id, leads.pessoa_id)).leftJoin(usuarios, eq(usuarios.id, leads.responsavel_id))
      .where(and(isNull(leads.excluido_em), eq(leads.finalidade, funil === 'VENDA' ? 'COMPRA' : 'LOCACAO'), this.escopo(user)))
      .orderBy(asc(leads.ordem), desc(leads.atualizado_em));
    return {
      funil,
      estagios: est.map((e) => ({
        id: e.id, nome: e.nome, ordem: e.ordem, cor: e.cor, tipo: e.tipo,
        leads: rows.filter((x) => x.l.estagio_id === e.id).map((x) => ({
          id: x.l.id, pessoa: x.p, finalidade: x.l.finalidade, origem: x.l.origem, responsavel: x.r?.id ? x.r : null,
          orcamento_max: x.l.orcamento_max, proxima_acao_em: x.l.proxima_acao_em, ordem: x.l.ordem,
        })),
      })),
    };
  }

  /* ------------------------------ Leads ------------------------------ */
  async listar(user: AuthUser, q: z.infer<typeof listaLeads>) {
    const termo = q.q ? `%${likeLiteral(q.q)}%` : undefined;
    const where = and(isNull(leads.excluido_em), this.escopo(user), q.estagio_id ? eq(leads.estagio_id, q.estagio_id) : undefined,
      q.finalidade ? eq(leads.finalidade, q.finalidade) : undefined, termo ? or(ilike(pessoas.nome_razao, termo), ilike(pessoas.email, termo), ilike(pessoas.telefone, termo)) : undefined);
    const [{ total }] = await this.db.select({ total: sql<number>`count(*)::int` }).from(leads).innerJoin(pessoas, eq(pessoas.id, leads.pessoa_id)).where(where);
    const rows = await this.db.select({ l: leads, p: { id: pessoas.id, nome_razao: pessoas.nome_razao, telefone: pessoas.telefone, email: pessoas.email }, e: { id: funil_estagios.id, nome: funil_estagios.nome, cor: funil_estagios.cor } })
      .from(leads).innerJoin(pessoas, eq(pessoas.id, leads.pessoa_id)).innerJoin(funil_estagios, eq(funil_estagios.id, leads.estagio_id))
      .where(where).orderBy(desc(leads.atualizado_em)).limit(q.pageSize).offset(offset(q));
    return paginado(rows.map((r) => ({ ...r.l, pessoa: r.p, estagio: r.e })), total, q);
  }

  async obter(user: AuthUser, id: string) {
    const [x] = await this.db.select({ l: leads, p: pessoas, e: funil_estagios, r: { id: usuarios.id, nome: usuarios.nome } })
      .from(leads).innerJoin(pessoas, eq(pessoas.id, leads.pessoa_id)).innerJoin(funil_estagios, eq(funil_estagios.id, leads.estagio_id))
      .leftJoin(usuarios, eq(usuarios.id, leads.responsavel_id)).where(and(eq(leads.id, id), isNull(leads.excluido_em), this.escopo(user)));
    if (!x) throw new NotFoundException('Lead não encontrado');
    const interesse = await this.db.select({ id: imoveis.id, codigo: imoveis.codigo, titulo: imoveis.titulo, situacao: imoveis.situacao })
      .from(lead_imoveis_interesse).innerJoin(imoveis, eq(imoveis.id, lead_imoveis_interesse.imovel_id)).where(eq(lead_imoveis_interesse.lead_id, id));
    return { ...x.l, pessoa: x.p, estagio: x.e, responsavel: x.r?.id ? x.r : null, imoveis_interesse: interesse };
  }

  private async estagioInicial(funil: Funil) {
    const [e] = await this.db.select().from(funil_estagios).where(and(eq(funil_estagios.funil, funil), eq(funil_estagios.ativo, true), eq(funil_estagios.tipo, 'ABERTO'))).orderBy(asc(funil_estagios.ordem)).limit(1);
    if (!e) throw new UnprocessableEntityException({ errors: { estagio_id: [`O funil ${funil} não tem estágios ativos`] } });
    return e;
  }

  async criar(user: AuthUser, b: z.infer<typeof leadCriar>, ip: string) {
    const { pessoa, pessoa_id, estagio_id, responsavel_id, ...dados } = b;
    const funil = funilDaFinalidade(dados.finalidade);
    const estagio = estagio_id ? await this.estagioDoFunil(estagio_id, funil) : await this.estagioInicial(funil);
    const id = await this.db.transaction(async (tx) => {
      let pid = pessoa_id ?? undefined;
      if (!pid && pessoa) {
        const [p] = await tx.insert(pessoas).values({ ...pessoa, criado_por: user.id }).returning({ id: pessoas.id });
        await tx.insert(pessoa_papeis).values({ pessoa_id: p.id, papel: 'CLIENTE' });
        pid = p.id;
      } else if (pid) {
        await tx.insert(pessoa_papeis).values({ pessoa_id: pid, papel: 'CLIENTE' }).onConflictDoNothing();
      }
      const [l] = await tx.insert(leads).values({ ...dados, pessoa_id: pid!, estagio_id: estagio.id, responsavel_id: user.escopo_proprio ? user.id : (responsavel_id ?? user.id) }).returning({ id: leads.id });
      await tx.insert(lead_historico_estagio).values({ lead_id: l.id, estagio_novo_id: estagio.id, usuario_id: user.id });
      return l.id;
    });
    const out = await this.obter(user, id);
    await this.audit.log({ usuario_id: user.id, entidade: 'leads', entidade_id: id, acao: 'CRIAR', depois: out, ip });
    return out;
  }

  async atualizar(user: AuthUser, id: string, b: z.infer<typeof leadAtualizar>, ip: string) {
    const antes = await this.obter(user, id);
    // troca de estágio só por PATCH /leads/:id/estagio (gera histórico): estagio_id é descartado aqui
    const { responsavel_id, estagio_id: _descartado, ...dados } = b;
    const set = { ...dados, ...(responsavel_id && !user.escopo_proprio ? { responsavel_id } : {}) };
    if (Object.keys(set).length) await this.db.update(leads).set(set).where(eq(leads.id, id));
    const depois = await this.obter(user, id);
    await this.audit.log({ usuario_id: user.id, entidade: 'leads', entidade_id: id, acao: 'ATUALIZAR', antes, depois, ip });
    return depois;
  }

  async excluir(user: AuthUser, id: string, ip: string) {
    const antes = await this.obter(user, id);
    await this.db.update(leads).set({ excluido_em: new Date() }).where(eq(leads.id, id));
    await this.audit.log({ usuario_id: user.id, entidade: 'leads', entidade_id: id, acao: 'EXCLUIR', antes, ip });
  }

  private async estagioDoFunil(estagioId: string, funil: Funil) {
    const [e] = await this.db.select().from(funil_estagios).where(eq(funil_estagios.id, estagioId));
    if (!e || !e.ativo) throw new UnprocessableEntityException({ errors: { estagio_id: ['Estágio inexistente ou inativo'] } });
    if (e.funil !== funil) throw new UnprocessableEntityException({ errors: { estagio_id: [`Este estágio pertence ao funil ${e.funil}`] } });
    return e;
  }

  /** Move o lead no funil, registra o histórico e exige motivo ao marcar como Perdido. */
  async mover(user: AuthUser, id: string, b: z.infer<typeof moverSchema>, ip: string) {
    const atual = await this.obter(user, id);
    const novo = await this.estagioDoFunil(b.estagio_id, funilDaFinalidade(atual.finalidade));
    if (novo.tipo === 'PERDIDO' && !b.motivo_perda) throw new UnprocessableEntityException({ errors: { motivo_perda: ['Informe o motivo da perda'] } });
    await this.db.transaction(async (tx) => {
      const [{ prox }] = await tx.select({ prox: sql<number>`coalesce(max(${leads.ordem}), -1) + 1` }).from(leads).where(eq(leads.estagio_id, novo.id));
      await tx.update(leads).set({ estagio_id: novo.id, ordem: b.ordem ?? Number(prox), motivo_perda: novo.tipo === 'PERDIDO' ? b.motivo_perda : null }).where(eq(leads.id, id));
      if (novo.id !== atual.estagio_id) await tx.insert(lead_historico_estagio).values({ lead_id: id, estagio_anterior_id: atual.estagio_id, estagio_novo_id: novo.id, usuario_id: user.id });
    });
    const depois = await this.obter(user, id);
    await this.audit.log({ usuario_id: user.id, entidade: 'leads', entidade_id: id, acao: 'ATUALIZAR', antes: { estagio: atual.estagio.nome }, depois: { estagio: novo.nome, motivo_perda: b.motivo_perda }, ip });
    return depois;
  }

  async historico(user: AuthUser, id: string) {
    await this.obter(user, id);
    const rows = await this.db.execute(sql`
      select h.id, h.criado_em, a.nome as estagio_anterior, n.nome as estagio_novo, u.nome as usuario
        from lead_historico_estagio h left join funil_estagios a on a.id = h.estagio_anterior_id
        join funil_estagios n on n.id = h.estagio_novo_id left join usuarios u on u.id = h.usuario_id
       where h.lead_id = ${id}::uuid order by h.criado_em desc`);
    return rows.rows;
  }

  async interacoes(user: AuthUser, id: string) {
    await this.obter(user, id);
    const rows = await this.db.select({ i: lead_interacoes, u: { id: usuarios.id, nome: usuarios.nome } }).from(lead_interacoes)
      .leftJoin(usuarios, eq(usuarios.id, lead_interacoes.usuario_id)).where(eq(lead_interacoes.lead_id, id)).orderBy(desc(lead_interacoes.criado_em));
    return rows.map((r) => ({ ...r.i, usuario: r.u?.id ? r.u : null }));
  }
  async criarInteracao(user: AuthUser, id: string, b: z.infer<typeof interacaoSchema>) {
    await this.obter(user, id);
    const [i] = await this.db.insert(lead_interacoes).values({ ...b, lead_id: id, usuario_id: user.id }).returning();
    await this.db.update(leads).set({ atualizado_em: new Date() }).where(eq(leads.id, id));
    return i;
  }
  async setInteresse(user: AuthUser, id: string, imoveisIds: string[]) {
    await this.obter(user, id);
    await this.db.transaction(async (tx) => {
      await tx.delete(lead_imoveis_interesse).where(eq(lead_imoveis_interesse.lead_id, id));
      if (imoveisIds.length) await tx.insert(lead_imoveis_interesse).values([...new Set(imoveisIds)].map((imovel_id) => ({ lead_id: id, imovel_id }))).onConflictDoNothing();
    });
    return (await this.obter(user, id)).imoveis_interesse;
  }

  /* ----------------------- Estágios (configuráveis) ----------------------- */
  estagios(funil?: Funil) {
    return this.db.select().from(funil_estagios).where(funil ? eq(funil_estagios.funil, funil) : undefined).orderBy(asc(funil_estagios.funil), asc(funil_estagios.ordem));
  }
  async criarEstagio(b: z.infer<typeof estagioCriar>) {
    const [e] = await this.db.insert(funil_estagios).values(b).returning();
    return e;
  }
  async atualizarEstagio(id: string, b: z.infer<typeof estagioAtualizar>) {
    const [e] = await this.db.update(funil_estagios).set(b).where(eq(funil_estagios.id, id)).returning();
    if (!e) throw new NotFoundException('Estágio não encontrado');
    return e;
  }
  async excluirEstagio(id: string) {
    const [{ n }] = await this.db.select({ n: sql<number>`count(*)::int` }).from(leads).where(and(eq(leads.estagio_id, id), isNull(leads.excluido_em)));
    if (n > 0) throw new ConflictException(`Há ${n} lead(s) neste estágio. Mova-os ou desative o estágio em vez de excluir.`);
    try { await this.db.delete(funil_estagios).where(eq(funil_estagios.id, id)); }
    catch { throw new ConflictException('Estágio com histórico: desative-o em vez de excluir.'); }
  }
  async reordenar(itens: z.infer<typeof ordemSchema>) {
    await this.db.transaction(async (tx) => { for (const i of itens) await tx.update(funil_estagios).set({ ordem: i.ordem }).where(eq(funil_estagios.id, i.id)); });
    return this.estagios();
  }
}
