import { Inject, Injectable } from '@nestjs/common';
import { DB, Database, Tx } from '../db/db.module';
import { audit_log } from '../db/schema';

export interface EventoAuditoria {
  usuario_id?: string | null;
  entidade: string;
  entidade_id?: string | null;
  acao: 'CRIAR' | 'ATUALIZAR' | 'EXCLUIR' | 'LOGIN' | 'LOGOUT' | 'SENHA' | 'EXPORTAR' | string;
  antes?: unknown;
  depois?: unknown;
  ip?: string | null;
}

/** Trilha de auditoria (tabela somente-inserção). Obrigatória em toda escrita financeira. */
@Injectable()
export class AuditService {
  constructor(@Inject(DB) private readonly db: Database) {}

  async log(e: EventoAuditoria, tx?: Tx | Database) {
    await (tx ?? this.db).insert(audit_log).values({
      usuario_id: e.usuario_id ?? null,
      entidade: e.entidade,
      entidade_id: e.entidade_id ?? null,
      acao: e.acao,
      valores_antes: (e.antes ?? null) as never,
      valores_depois: (e.depois ?? null) as never,
      ip: e.ip ?? null,
    });
  }
}
