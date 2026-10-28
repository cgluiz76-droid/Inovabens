import { ConflictException, Inject, Injectable, NotFoundException } from '@nestjs/common';
import { asc, eq, sql } from 'drizzle-orm';
import { z } from 'zod';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { assertAcessoImovel } from '../common/escopo';
import { DB, Database } from '../db/db.module';
import { aquisicao_itens, aquisicoes } from '../db/schema';
import { aquisicaoSchema, itemAtualizarSchema, itemCriarSchema } from './financeiro.schemas';

@Injectable()
export class AquisicaoService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  /** Aquisição + itens. O `total_aquisicao` vem do banco (coluna gerada) — nunca é calculado/gravado pelo cliente. */
  async obter(user: AuthUser, imovelId: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const [a] = await this.db.select().from(aquisicoes).where(eq(aquisicoes.imovel_id, imovelId));
    if (!a) throw new NotFoundException('Aquisição ainda não cadastrada para este imóvel');
    const itens = await this.db.select().from(aquisicao_itens).where(eq(aquisicao_itens.aquisicao_id, a.id)).orderBy(asc(aquisicao_itens.criado_em));
    return { ...a, itens };
  }

  async salvar(user: AuthUser, imovelId: string, b: z.infer<typeof aquisicaoSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const [antes] = await this.db.select().from(aquisicoes).where(eq(aquisicoes.imovel_id, imovelId));
    if (!antes) {
      await this.db.insert(aquisicoes).values({ ...b, imovel_id: imovelId });
    } else {
      // Se já existem itens detalhados, eles mandam em impostos/taxas/despesas (trigger); só os demais campos mudam.
      const [{ n }] = await this.db.select({ n: sql<number>`count(*)::int` }).from(aquisicao_itens).where(eq(aquisicao_itens.aquisicao_id, antes.id));
      const { impostos, taxas, despesas_diversas, ...resto } = b;
      await this.db.update(aquisicoes).set(n > 0 ? resto : { ...resto, impostos, taxas, despesas_diversas }).where(eq(aquisicoes.id, antes.id));
    }
    const depois = await this.obter(user, imovelId);
    await this.audit.log({ usuario_id: user.id, entidade: 'aquisicoes', entidade_id: depois.id, acao: antes ? 'ATUALIZAR' : 'CRIAR', antes, depois, ip });
    return depois;
  }

  private async aquisicaoDoItem(user: AuthUser, itemId: string) {
    const [r] = await this.db.select({ item: aquisicao_itens, imovel_id: aquisicoes.imovel_id }).from(aquisicao_itens)
      .innerJoin(aquisicoes, eq(aquisicoes.id, aquisicao_itens.aquisicao_id)).where(eq(aquisicao_itens.id, itemId));
    if (!r) throw new NotFoundException('Item não encontrado');
    await assertAcessoImovel(this.db, user, r.imovel_id);
    return r;
  }

  async criarItem(user: AuthUser, imovelId: string, b: z.infer<typeof itemCriarSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const [a] = await this.db.select().from(aquisicoes).where(eq(aquisicoes.imovel_id, imovelId));
    if (!a) throw new ConflictException('Cadastre a aquisição (data e valor pago) antes de lançar itens.');
    const [item] = await this.db.insert(aquisicao_itens).values({ ...b, aquisicao_id: a.id }).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'aquisicao_itens', entidade_id: item.id, acao: 'CRIAR', depois: item, ip });
    return this.obter(user, imovelId);
  }
  async atualizarItem(user: AuthUser, itemId: string, b: z.infer<typeof itemAtualizarSchema>, ip: string) {
    const { item, imovel_id } = await this.aquisicaoDoItem(user, itemId);
    const [depois] = await this.db.update(aquisicao_itens).set(b).where(eq(aquisicao_itens.id, itemId)).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'aquisicao_itens', entidade_id: itemId, acao: 'ATUALIZAR', antes: item, depois, ip });
    return this.obter(user, imovel_id);
  }
  async excluirItem(user: AuthUser, itemId: string, ip: string) {
    const { item, imovel_id } = await this.aquisicaoDoItem(user, itemId);
    await this.db.delete(aquisicao_itens).where(eq(aquisicao_itens.id, itemId));
    await this.audit.log({ usuario_id: user.id, entidade: 'aquisicao_itens', entidade_id: itemId, acao: 'EXCLUIR', antes: item, ip });
    return this.obter(user, imovel_id);
  }
}
