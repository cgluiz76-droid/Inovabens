import { Controller, Get, Global, Inject, Module, Query } from '@nestjs/common';
import { and, desc, eq, sql } from 'drizzle-orm';
import { z } from 'zod';
import { RequirePermission } from '../common/decorators';
import { offset, paginacaoSchema, paginado } from '../common/pagination';
import { ZodPipe } from '../common/zod.pipe';
import { DB, Database } from '../db/db.module';
import { audit_log, usuarios } from '../db/schema';
import { AuditService } from './audit.service';

const filtro = paginacaoSchema.extend({ entidade: z.string().max(80).optional(), entidade_id: z.string().uuid().optional() });

@Controller('auditoria')
class AuditoriaController {
  constructor(@Inject(DB) private readonly db: Database) {}

  @Get()
  @RequirePermission('auditoria:ver')
  async listar(@Query(new ZodPipe(filtro)) q: z.infer<typeof filtro>) {
    const where = and(q.entidade ? eq(audit_log.entidade, q.entidade) : undefined, q.entidade_id ? eq(audit_log.entidade_id, q.entidade_id) : undefined);
    const [{ total }] = await this.db.select({ total: sql<number>`count(*)::int` }).from(audit_log).where(where);
    const rows = await this.db
      .select({ log: audit_log, usuario: { id: usuarios.id, nome: usuarios.nome } })
      .from(audit_log).leftJoin(usuarios, eq(usuarios.id, audit_log.usuario_id))
      .where(where).orderBy(desc(audit_log.criado_em)).limit(q.pageSize).offset(offset(q));
    return paginado(rows.map((r) => ({ ...r.log, usuario: r.usuario?.id ? r.usuario : null })), total, q);
  }
}

@Global()
@Module({ controllers: [AuditoriaController], providers: [AuditService], exports: [AuditService] })
export class AuditoriaModule {}
