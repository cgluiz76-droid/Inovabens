import { ForbiddenException, NotFoundException } from '@nestjs/common';
import { and, eq, isNull } from 'drizzle-orm';
import type { Database } from '../db/db.module';
import { imoveis } from '../db/schema';
import type { AuthUser } from './auth.types';

/**
 * Garante que o imóvel existe (e não foi excluído) e que o usuário pode acessá-lo:
 * perfis com `escopo_proprio` (Corretor) só acessam imóveis em que são o responsável.
 * Retorna o imóvel. Use em TODA rota que opera "dentro" de um imóvel (financeiro, agenda, memorandos...).
 */
export async function assertAcessoImovel(db: Database, user: AuthUser, imovelId: string) {
  const [im] = await db.select().from(imoveis).where(and(eq(imoveis.id, imovelId), isNull(imoveis.excluido_em))).limit(1);
  if (!im) throw new NotFoundException('Imóvel não encontrado');
  if (user.escopo_proprio && im.corretor_responsavel_id !== user.id) throw new ForbiddenException('Sem acesso a este imóvel');
  return im;
}
