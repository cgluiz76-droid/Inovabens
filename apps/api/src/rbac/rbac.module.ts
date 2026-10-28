import {
  Body, ConflictException, Controller, Delete, Get, HttpCode, Inject, Injectable, Module, NotFoundException, Param, ParseUUIDPipe,
  Patch, Post, UnprocessableEntityException,
} from '@nestjs/common';
import { eq, inArray, sql } from 'drizzle-orm';
import { z } from 'zod';
import { PERMISSOES } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { ZodPipe } from '../common/zod.pipe';
import { DB, Database } from '../db/db.module';
import { perfil_permissoes, perfis, permissoes, usuarios } from '../db/schema';

const PERFIL_ADMIN = 'Administrador';
const roleSchema = z.object({
  nome: z.string().trim().min(2).max(60),
  descricao: z.string().trim().max(300).nullish(),
  escopo_proprio: z.boolean().default(false),
  permissoes: z.array(z.enum(PERMISSOES)).default([]),
});
const rolePatch = roleSchema.partial();

@Injectable()
export class RbacService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  async listar() {
    const rs = await this.db.select().from(perfis).orderBy(perfis.nome);
    const links = await this.db.select({ perfil_id: perfil_permissoes.perfil_id, codigo: permissoes.codigo }).from(perfil_permissoes)
      .innerJoin(permissoes, eq(permissoes.id, perfil_permissoes.permissao_id));
    const cont = await this.db.select({ perfil_id: usuarios.perfil_id, n: sql<number>`count(*)::int` }).from(usuarios).groupBy(usuarios.perfil_id);
    return rs.map((r) => ({
      ...r,
      permissoes: links.filter((l) => l.perfil_id === r.id).map((l) => l.codigo).sort(),
      usuarios_count: cont.find((c) => c.perfil_id === r.id)?.n ?? 0,
    }));
  }
  async obter(id: string) {
    const r = (await this.listar()).find((x) => x.id === id);
    if (!r) throw new NotFoundException('Perfil não encontrado');
    return r;
  }
  private async gravarPermissoes(perfilId: string, codigos: string[]) {
    await this.db.delete(perfil_permissoes).where(eq(perfil_permissoes.perfil_id, perfilId));
    if (!codigos.length) return;
    const ps = await this.db.select().from(permissoes).where(inArray(permissoes.codigo, codigos));
    await this.db.insert(perfil_permissoes).values(ps.map((p) => ({ perfil_id: perfilId, permissao_id: p.id })));
  }
  async criar(b: z.infer<typeof roleSchema>, u: AuthUser, ip: string) {
    const [r] = await this.db.insert(perfis).values({ nome: b.nome, descricao: b.descricao ?? null, escopo_proprio: b.escopo_proprio }).returning();
    await this.gravarPermissoes(r.id, b.permissoes);
    const out = await this.obter(r.id);
    await this.audit.log({ usuario_id: u.id, entidade: 'perfis', entidade_id: r.id, acao: 'CRIAR', depois: out, ip });
    return out;
  }
  async atualizar(id: string, b: z.infer<typeof rolePatch>, u: AuthUser, ip: string) {
    const antes = await this.obter(id);
    // trava de segurança: o perfil Administrador não pode perder acesso (evita se trancar fora do sistema)
    if (antes.nome === PERFIL_ADMIN && (b.permissoes || (b.nome && b.nome !== PERFIL_ADMIN) || b.escopo_proprio)) {
      throw new UnprocessableEntityException({ errors: { _: ['O perfil Administrador não pode ser alterado'] } });
    }
    const { permissoes: perms, ...campos } = b;
    if (Object.keys(campos).length) await this.db.update(perfis).set({ ...campos, descricao: campos.descricao ?? undefined }).where(eq(perfis.id, id));
    if (perms) await this.gravarPermissoes(id, perms);
    const depois = await this.obter(id);
    await this.audit.log({ usuario_id: u.id, entidade: 'perfis', entidade_id: id, acao: 'ATUALIZAR', antes, depois, ip });
    return depois;
  }
  async excluir(id: string, u: AuthUser, ip: string) {
    const r = await this.obter(id);
    if (r.nome === PERFIL_ADMIN) throw new UnprocessableEntityException({ errors: { _: ['O perfil Administrador não pode ser excluído'] } });
    if (r.usuarios_count > 0) throw new ConflictException('Existem usuários neste perfil. Mova-os antes de excluir.');
    await this.db.delete(perfis).where(eq(perfis.id, id));
    await this.audit.log({ usuario_id: u.id, entidade: 'perfis', entidade_id: id, acao: 'EXCLUIR', antes: r, ip });
  }
  catalogo() { return this.db.select().from(permissoes).orderBy(permissoes.codigo); }
}

@Controller()
class RbacController {
  constructor(private readonly s: RbacService) {}
  @Get('permissoes') @RequirePermission('rbac:gerir') permissoes() { return this.s.catalogo(); }
  @Get('roles') @RequirePermission('rbac:gerir') listar() { return this.s.listar(); }
  @Post('roles') @RequirePermission('rbac:gerir')
  criar(@Body(new ZodPipe(roleSchema)) b: z.infer<typeof roleSchema>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.criar(b, u, ip); }
  @Get('roles/:id') @RequirePermission('rbac:gerir') obter(@Param('id', ParseUUIDPipe) id: string) { return this.s.obter(id); }
  @Patch('roles/:id') @RequirePermission('rbac:gerir')
  atualizar(@Param('id', ParseUUIDPipe) id: string, @Body(new ZodPipe(rolePatch)) b: z.infer<typeof rolePatch>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.atualizar(id, b, u, ip); }
  @Delete('roles/:id') @HttpCode(204) @RequirePermission('rbac:gerir')
  excluir(@Param('id', ParseUUIDPipe) id: string, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.excluir(id, u, ip); }
}

@Module({ controllers: [RbacController], providers: [RbacService], exports: [RbacService] })
export class RbacModule {}
