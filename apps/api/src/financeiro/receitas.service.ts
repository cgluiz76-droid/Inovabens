import { Inject, Injectable, NotFoundException } from '@nestjs/common';
import { and, asc, eq, isNull } from 'drizzle-orm';
import { z } from 'zod';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { assertAcessoImovel } from '../common/escopo';
import { DB, Database } from '../db/db.module';
import { receitas } from '../db/schema';
import { receitaAtualizarSchema, receitaCriarSchema } from './financeiro.schemas';

@Injectable()
export class ReceitasService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  async listar(user: AuthUser, imovelId: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    return this.db.select().from(receitas).where(and(eq(receitas.imovel_id, imovelId), isNull(receitas.excluido_em))).orderBy(asc(receitas.data));
  }
  async criar(user: AuthUser, imovelId: string, b: z.infer<typeof receitaCriarSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const [r] = await this.db.insert(receitas).values({ ...b, imovel_id: imovelId }).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'receitas', entidade_id: r.id, acao: 'CRIAR', depois: r, ip });
    return r;
  }
  private async doUsuario(user: AuthUser, id: string) {
    const [r] = await this.db.select().from(receitas).where(and(eq(receitas.id, id), isNull(receitas.excluido_em)));
    if (!r) throw new NotFoundException('Receita não encontrada');
    await assertAcessoImovel(this.db, user, r.imovel_id);
    return r;
  }
  async atualizar(user: AuthUser, id: string, b: z.infer<typeof receitaAtualizarSchema>, ip: string) {
    const antes = await this.doUsuario(user, id);
    const [r] = await this.db.update(receitas).set(b).where(eq(receitas.id, id)).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'receitas', entidade_id: id, acao: 'ATUALIZAR', antes, depois: r, ip });
    return r;
  }
  async excluir(user: AuthUser, id: string, ip: string) {
    const antes = await this.doUsuario(user, id);
    await this.db.update(receitas).set({ excluido_em: new Date() }).where(eq(receitas.id, id));
    await this.audit.log({ usuario_id: user.id, entidade: 'receitas', entidade_id: id, acao: 'EXCLUIR', antes, ip });
  }
}
