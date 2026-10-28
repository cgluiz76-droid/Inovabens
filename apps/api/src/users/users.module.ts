import {
  Body, Controller, Get, Inject, Injectable, Module, NotFoundException, Param, ParseUUIDPipe, Patch, Post, Query, UnprocessableEntityException,
} from '@nestjs/common';
import { hash } from '@node-rs/argon2';
import { and, asc, eq, isNull, or, sql, ilike } from 'drizzle-orm';
import { z } from 'zod';
import { AuditService } from '../auditoria/audit.service';
import { AuthService } from '../auth/auth.service';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { offset, paginacaoSchema, paginado } from '../common/pagination';
import { likeLiteral, senhaForte, uuid } from '../common/schemas';
import { ZodPipe } from '../common/zod.pipe';
import { DB, Database } from '../db/db.module';
import { perfis, usuarios } from '../db/schema';

const criarSchema = z.object({
  nome: z.string().trim().min(2).max(160),
  email: z.string().trim().toLowerCase().email().max(200),
  senha: senhaForte,
  perfil_id: uuid,
  ativo: z.boolean().default(true),
});
const atualizarSchema = z.object({
  nome: z.string().trim().min(2).max(160), email: z.string().trim().toLowerCase().email().max(200),
  senha: senhaForte, perfil_id: uuid, ativo: z.boolean(),
}).partial();
const statusSchema = z.object({ ativo: z.boolean() });
const listaSchema = paginacaoSchema.extend({ q: z.string().trim().max(100).optional() });

const colunas = {
  id: usuarios.id, nome: usuarios.nome, email: usuarios.email, ativo: usuarios.ativo, totp_ativo: usuarios.totp_ativo,
  ultimo_login_em: usuarios.ultimo_login_em, criado_em: usuarios.criado_em,
  perfil: { id: perfis.id, nome: perfis.nome },
};

@Injectable()
export class UsersService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService, private readonly auth: AuthService) {}

  async listar(q: z.infer<typeof listaSchema>) {
    const termo = q.q ? `%${likeLiteral(q.q)}%` : undefined;
    const where = and(isNull(usuarios.excluido_em), termo ? or(ilike(usuarios.nome, termo), ilike(usuarios.email, termo)) : undefined);
    const [{ total }] = await this.db.select({ total: sql<number>`count(*)::int` }).from(usuarios).where(where);
    const rows = await this.db.select(colunas).from(usuarios).innerJoin(perfis, eq(perfis.id, usuarios.perfil_id))
      .where(where).orderBy(asc(usuarios.nome)).limit(q.pageSize).offset(offset(q));
    return paginado(rows, total, q);
  }
  async obter(id: string) {
    const [r] = await this.db.select(colunas).from(usuarios).innerJoin(perfis, eq(perfis.id, usuarios.perfil_id))
      .where(and(eq(usuarios.id, id), isNull(usuarios.excluido_em)));
    if (!r) throw new NotFoundException('Usuário não encontrado');
    return r;
  }
  /** Usuários ativos para <select> (responsável pelo imóvel/lead/compromisso). Qualquer usuário autenticado. */
  opcoes() {
    return this.db.select({ id: usuarios.id, nome: usuarios.nome, perfil: perfis.nome, escopo_proprio: perfis.escopo_proprio })
      .from(usuarios).innerJoin(perfis, eq(perfis.id, usuarios.perfil_id))
      .where(and(eq(usuarios.ativo, true), isNull(usuarios.excluido_em))).orderBy(asc(usuarios.nome));
  }
  async criar(b: z.infer<typeof criarSchema>, u: AuthUser, ip: string) {
    const [r] = await this.db.insert(usuarios).values({ nome: b.nome, email: b.email, senha_hash: await hash(b.senha), perfil_id: b.perfil_id, ativo: b.ativo }).returning({ id: usuarios.id });
    const out = await this.obter(r.id);
    await this.audit.log({ usuario_id: u.id, entidade: 'usuarios', entidade_id: r.id, acao: 'CRIAR', depois: out, ip });
    return out;
  }
  async atualizar(id: string, b: z.infer<typeof atualizarSchema>, u: AuthUser, ip: string) {
    const antes = await this.obter(id);
    if (id === u.id && (b.ativo === false || (b.perfil_id && b.perfil_id !== antes.perfil.id))) {
      throw new UnprocessableEntityException({ errors: { _: ['Você não pode desativar ou trocar o perfil da sua própria conta'] } });
    }
    const { senha, ...campos } = b;
    const set = { ...campos, ...(senha ? { senha_hash: await hash(senha), tentativas_falhas: 0, bloqueado_ate: null } : {}) };
    if (Object.keys(set).length) await this.db.update(usuarios).set(set).where(eq(usuarios.id, id));
    if (senha || b.ativo === false || b.perfil_id) await this.auth.revogarTodas(id); // permissões vão no JWT: força novo login
    const depois = await this.obter(id);
    await this.audit.log({ usuario_id: u.id, entidade: 'usuarios', entidade_id: id, acao: 'ATUALIZAR', antes, depois: { ...depois, senha_alterada: !!senha }, ip });
    return depois;
  }
}

@Controller('users')
class UsersController {
  constructor(private readonly s: UsersService) {}
  @Get('options') opcoes() { return this.s.opcoes(); } // antes de :id
  @Get() @RequirePermission('usuarios:gerir') listar(@Query(new ZodPipe(listaSchema)) q: z.infer<typeof listaSchema>) { return this.s.listar(q); }
  @Post() @RequirePermission('usuarios:gerir')
  criar(@Body(new ZodPipe(criarSchema)) b: z.infer<typeof criarSchema>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.criar(b, u, ip); }
  @Get(':id') @RequirePermission('usuarios:gerir') obter(@Param('id', ParseUUIDPipe) id: string) { return this.s.obter(id); }
  @Patch(':id') @RequirePermission('usuarios:gerir')
  atualizar(@Param('id', ParseUUIDPipe) id: string, @Body(new ZodPipe(atualizarSchema)) b: z.infer<typeof atualizarSchema>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.atualizar(id, b, u, ip); }
  @Patch(':id/status') @RequirePermission('usuarios:gerir')
  status(@Param('id', ParseUUIDPipe) id: string, @Body(new ZodPipe(statusSchema)) b: z.infer<typeof statusSchema>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.atualizar(id, { ativo: b.ativo }, u, ip); }
}

@Module({ controllers: [UsersController], providers: [UsersService], exports: [UsersService] })
export class UsersModule {}
