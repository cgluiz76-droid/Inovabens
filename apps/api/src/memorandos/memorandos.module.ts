import { Body, Controller, Delete, Get, HttpCode, Inject, Injectable, Module, NotFoundException, Param, ParseUUIDPipe, Patch, Post } from '@nestjs/common';
import { and, desc, eq, isNull } from 'drizzle-orm';
import { z } from 'zod';
import { TIPO_MEMORANDO } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { assertAcessoImovel } from '../common/escopo';
import { ZodPipe } from '../common/zod.pipe';
import { DB, Database } from '../db/db.module';
import { memorandos, usuarios } from '../db/schema';

const base = z.object({
  tipo: z.enum(TIPO_MEMORANDO).default('MEMORANDO'),
  titulo: z.string().trim().min(2).max(200),
  corpo: z.string().default(''),
  fixado: z.boolean().default(false),
});
const criar = base;
const atualizar = base.partial();

@Injectable()
export class MemorandosService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  async listar(user: AuthUser, imovelId: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const rows = await this.db.select({ m: memorandos, autor: { id: usuarios.id, nome: usuarios.nome } }).from(memorandos)
      .innerJoin(usuarios, eq(usuarios.id, memorandos.autor_id))
      .where(and(eq(memorandos.imovel_id, imovelId), isNull(memorandos.excluido_em))).orderBy(desc(memorandos.fixado), desc(memorandos.criado_em));
    return rows.map((r) => ({ ...r.m, autor: r.autor }));
  }
  async criar(user: AuthUser, imovelId: string, b: z.infer<typeof criar>, ip: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const [m] = await this.db.insert(memorandos).values({ ...b, imovel_id: imovelId, autor_id: user.id }).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'memorandos', entidade_id: m.id, acao: 'CRIAR', depois: m, ip });
    return { ...m, autor: { id: user.id, nome: user.nome } };
  }
  private async doUsuario(user: AuthUser, id: string) {
    const [m] = await this.db.select().from(memorandos).where(and(eq(memorandos.id, id), isNull(memorandos.excluido_em)));
    if (!m) throw new NotFoundException('Memorando não encontrado');
    await assertAcessoImovel(this.db, user, m.imovel_id);
    return m;
  }
  async atualizar(user: AuthUser, id: string, b: z.infer<typeof atualizar>, ip: string) {
    const antes = await this.doUsuario(user, id);
    const [m] = await this.db.update(memorandos).set(b).where(eq(memorandos.id, id)).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'memorandos', entidade_id: id, acao: 'ATUALIZAR', antes, depois: m, ip }); // histórico das edições
    return m;
  }
  async excluir(user: AuthUser, id: string, ip: string) {
    const antes = await this.doUsuario(user, id);
    await this.db.update(memorandos).set({ excluido_em: new Date() }).where(eq(memorandos.id, id));
    await this.audit.log({ usuario_id: user.id, entidade: 'memorandos', entidade_id: id, acao: 'EXCLUIR', antes, ip });
  }
}

@Controller()
class MemorandosController {
  constructor(private readonly s: MemorandosService) {}
  @Get('imoveis/:id/memorandos') @RequirePermission('memorandos:ver')
  listar(@CurrentUser() u: AuthUser, @Param('id', ParseUUIDPipe) id: string) { return this.s.listar(u, id); }
  @Post('imoveis/:id/memorandos') @RequirePermission('memorandos:criar')
  criar(@CurrentUser() u: AuthUser, @Param('id', ParseUUIDPipe) id: string, @Body(new ZodPipe(criar)) b: z.infer<typeof criar>, @ClientIp() ip: string) { return this.s.criar(u, id, b, ip); }
  @Patch('memorandos/:id') @RequirePermission('memorandos:editar')
  atualizar(@CurrentUser() u: AuthUser, @Param('id', ParseUUIDPipe) id: string, @Body(new ZodPipe(atualizar)) b: z.infer<typeof atualizar>, @ClientIp() ip: string) { return this.s.atualizar(u, id, b, ip); }
  @Delete('memorandos/:id') @HttpCode(204) @RequirePermission('memorandos:excluir')
  excluir(@CurrentUser() u: AuthUser, @Param('id', ParseUUIDPipe) id: string, @ClientIp() ip: string) { return this.s.excluir(u, id, ip); }
}

@Module({ controllers: [MemorandosController], providers: [MemorandosService], exports: [MemorandosService] })
export class MemorandosModule {}
