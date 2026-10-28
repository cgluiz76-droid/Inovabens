import { Inject, Injectable, Logger, NotFoundException } from '@nestjs/common';
import { and, asc, eq, gte, inArray, isNull, lte, or, SQL } from 'drizzle-orm';
import { z } from 'zod';
import { STATUS_COMPROMISSO, TIPO_COMPROMISSO } from '@inovabens/shared';
import { AlertasService } from '../alertas/alertas.module';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { assertAcessoImovel } from '../common/escopo';
import { blank, uuid } from '../common/schemas';
import { DB, Database } from '../db/db.module';
import { alertas, compromisso_participantes, compromissos, imoveis, usuarios } from '../db/schema';
import { GoogleService } from './google.service';

const base = z.object({
  tipo: z.enum(TIPO_COMPROMISSO).default('TAREFA'),
  titulo: z.string().trim().min(2).max(200),
  descricao: blank(z.string().trim()),
  local: blank(z.string().trim().max(200)),
  inicio_em: z.coerce.date(),
  fim_em: z.coerce.date(),
  lead_id: blank(uuid),
  responsavel_id: blank(uuid),
  participantes: z.array(z.object({ email: z.string().trim().toLowerCase().email() })).max(30).default([]),
});
const periodoOk = (v: { inicio_em?: Date; fim_em?: Date }) => !v.inicio_em || !v.fim_em || v.fim_em >= v.inicio_em;
export const compromissoCriar = base.refine(periodoOk, { path: ['fim_em'], message: 'O término deve ser depois do início' });
export const compromissoAtualizar = base.partial().extend({ status: z.enum(STATUS_COMPROMISSO).optional() })
  .refine(periodoOk, { path: ['fim_em'], message: 'O término deve ser depois do início' });
export const listaSchema = z.object({
  de: z.coerce.date().optional(), ate: z.coerce.date().optional(),
  imovel_id: uuid.optional(), responsavel_id: uuid.optional(), status: z.enum(STATUS_COMPROMISSO).optional(),
});
export const reagendarSchema = z.object({ inicio_em: z.coerce.date(), fim_em: z.coerce.date() })
  .refine(periodoOk, { path: ['fim_em'], message: 'O término deve ser depois do início' });

@Injectable()
export class AgendaService {
  private readonly log = new Logger('Agenda');
  constructor(
    @Inject(DB) private readonly db: Database, private readonly audit: AuditService,
    private readonly alertas: AlertasService, private readonly google: GoogleService,
  ) {}

  /** Sincroniza com o Google sem bloquear nem quebrar a operação principal. */
  private empurrar(id: string) { void this.google.empurrar(id).catch((e) => this.log.warn(`push Google: ${e}`)); }

  private async enriquecer<T extends { id: string }>(rows: T[]) {
    if (!rows.length) return [];
    const ps = await this.db.select().from(compromisso_participantes).where(inArray(compromisso_participantes.compromisso_id, rows.map((r) => r.id)));
    return rows.map((r) => ({ ...r, participantes: ps.filter((p) => p.compromisso_id === r.id).map((p) => ({ email: p.email, usuario_id: p.usuario_id, pessoa_id: p.pessoa_id, resposta: p.resposta })) }));
  }

  private async consultar(user: AuthUser, extra: (SQL | undefined)[]) {
    const rows = await this.db.select({ c: compromissos, imovel: { id: imoveis.id, codigo: imoveis.codigo, titulo: imoveis.titulo }, resp: { id: usuarios.id, nome: usuarios.nome } })
      .from(compromissos).innerJoin(imoveis, eq(imoveis.id, compromissos.imovel_id)).innerJoin(usuarios, eq(usuarios.id, compromissos.responsavel_id))
      .where(and(isNull(compromissos.excluido_em), isNull(imoveis.excluido_em),
        user.escopo_proprio ? or(eq(compromissos.responsavel_id, user.id), eq(imoveis.corretor_responsavel_id, user.id)) : undefined, ...extra))
      .orderBy(asc(compromissos.inicio_em));
    return this.enriquecer(rows.map((r) => ({ ...r.c, imovel: r.imovel, responsavel: r.resp })));
  }

  listar(user: AuthUser, q: z.infer<typeof listaSchema>) {
    return this.consultar(user, [
      q.de ? gte(compromissos.fim_em, q.de) : undefined, q.ate ? lte(compromissos.inicio_em, q.ate) : undefined,
      q.imovel_id ? eq(compromissos.imovel_id, q.imovel_id) : undefined, q.responsavel_id ? eq(compromissos.responsavel_id, q.responsavel_id) : undefined,
      q.status ? eq(compromissos.status, q.status) : undefined,
    ]);
  }
  async listarDoImovel(user: AuthUser, imovelId: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    return this.consultar(user, [eq(compromissos.imovel_id, imovelId)]);
  }
  async obter(user: AuthUser, id: string) {
    const r = (await this.consultar(user, [eq(compromissos.id, id)]))[0];
    if (!r) throw new NotFoundException('Compromisso não encontrado');
    return r;
  }

  private async gravarParticipantes(id: string, itens: { email: string }[]) {
    await this.db.delete(compromisso_participantes).where(eq(compromisso_participantes.compromisso_id, id));
    if (itens.length) await this.db.insert(compromisso_participantes).values([...new Set(itens.map((i) => i.email))].map((email) => ({ compromisso_id: id, email })));
  }

  async criar(user: AuthUser, imovelId: string, b: z.infer<typeof compromissoCriar>, ip: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const { participantes, responsavel_id, ...dados } = b;
    const [c] = await this.db.insert(compromissos).values({ ...dados, imovel_id: imovelId, responsavel_id: user.escopo_proprio ? user.id : (responsavel_id ?? user.id) }).returning();
    await this.gravarParticipantes(c.id, participantes);
    await this.audit.log({ usuario_id: user.id, entidade: 'compromissos', entidade_id: c.id, acao: 'CRIAR', depois: c, ip });
    this.empurrar(c.id);
    return this.obter(user, c.id);
  }

  async atualizar(user: AuthUser, id: string, b: z.infer<typeof compromissoAtualizar>, ip: string) {
    const antes = await this.obter(user, id);
    const { participantes, responsavel_id, status, ...dados } = b;
    const set: Partial<typeof compromissos.$inferInsert> = { ...dados };
    if (responsavel_id && !user.escopo_proprio) set.responsavel_id = responsavel_id;
    if (status) { set.status = status; set.concluido_em = status === 'CONCLUIDO' ? new Date() : null; }
    if (Object.keys(set).length) await this.db.update(compromissos).set(set).where(eq(compromissos.id, id));
    if (participantes) await this.gravarParticipantes(id, participantes);
    const depois = await this.obter(user, id);
    // Concluir, cancelar ou reagendar para o futuro encerra o alerta de "não cumprido"
    if (depois.status !== 'PENDENTE' || depois.fim_em > new Date()) await this.alertas.resolverDoCompromisso(id);
    await this.audit.log({ usuario_id: user.id, entidade: 'compromissos', entidade_id: id, acao: 'ATUALIZAR', antes, depois, ip });
    this.empurrar(id);
    return depois;
  }

  concluir(user: AuthUser, id: string, ip: string) { return this.atualizar(user, id, { status: 'CONCLUIDO' }, ip); }

  async excluir(user: AuthUser, id: string, ip: string) {
    const antes = await this.obter(user, id);
    await this.db.update(compromissos).set({ excluido_em: new Date() }).where(eq(compromissos.id, id));
    await this.alertas.resolverDoCompromisso(id);
    await this.audit.log({ usuario_id: user.id, entidade: 'compromissos', entidade_id: id, acao: 'EXCLUIR', antes, ip });
    void this.google.removerDoGoogle(antes.responsavel_id, antes.google_event_id).catch((e) => this.log.warn(`remover Google: ${e}`));
  }

  /** PATCH /alertas/:id/reagendar — novo horário; o alerta é encerrado porque o compromisso volta a ser futuro. */
  async reagendarPorAlerta(user: AuthUser, alertaId: string, b: z.infer<typeof reagendarSchema>, ip: string) {
    const [a] = await this.db.select().from(alertas).where(and(eq(alertas.id, alertaId), eq(alertas.usuario_id, user.id)));
    if (!a?.compromisso_id) throw new NotFoundException('Alerta de compromisso não encontrado');
    return this.atualizar(user, a.compromisso_id, { ...b, status: 'PENDENTE' }, ip);
  }
}
