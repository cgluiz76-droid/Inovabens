import {
  Body, Controller, Delete, Get, HttpCode, Inject, Injectable, Module, NotFoundException, Param, ParseUUIDPipe, Patch, Post, Query,
} from '@nestjs/common';
import { and, asc, eq, exists, ilike, inArray, isNull, or, sql } from 'drizzle-orm';
import { z } from 'zod';
import { PAPEL_PESSOA, TIPO_PESSOA } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, RequirePermission } from '../common/decorators';
import { offset, paginacaoSchema, paginado } from '../common/pagination';
import { blank, likeLiteral } from '../common/schemas';
import { ZodPipe } from '../common/zod.pipe';
import { DB, Database } from '../db/db.module';
import { pessoa_papeis, pessoas } from '../db/schema';

const txt = (n: number) => blank(z.string().trim().max(n));
const documento = blank(
  z.string().trim().transform((v) => v.replace(/\D/g, '')).refine((v) => v.length === 11 || v.length === 14, 'CPF (11 dígitos) ou CNPJ (14 dígitos)'),
);
const base = z.object({
  tipo: z.enum(TIPO_PESSOA).default('PF'),
  nome_razao: z.string().trim().min(2).max(200),
  cpf_cnpj: documento, rg: txt(30),
  email: blank(z.string().trim().toLowerCase().email().max(200)),
  telefone: txt(30), whatsapp: txt(30),
  cep: txt(12), logradouro: txt(200), numero: txt(20), complemento: txt(100), bairro: txt(100), cidade: txt(100),
  uf: blank(z.string().trim().length(2).transform((v) => v.toUpperCase())),
  observacoes: blank(z.string().trim()),
  lgpd_consentimento_em: blank(z.coerce.date()),
  papeis: z.array(z.object({ papel: z.enum(PAPEL_PESSOA), especialidade: txt(100) })).default([]),
});
export const pessoaCriar = base;
export const pessoaAtualizar = base.partial();
const listaSchema = paginacaoSchema.extend({ q: z.string().trim().max(100).optional(), papel: z.enum(PAPEL_PESSOA).optional() });

@Injectable()
export class PessoasService {
  constructor(@Inject(DB) private readonly db: Database, private readonly audit: AuditService) {}

  private async comPapeis<T extends { id: string }>(rows: T[]) {
    if (!rows.length) return [];
    const ps = await this.db.select().from(pessoa_papeis).where(inArray(pessoa_papeis.pessoa_id, rows.map((r) => r.id)));
    return rows.map((r) => ({ ...r, papeis: ps.filter((p) => p.pessoa_id === r.id).map((p) => ({ papel: p.papel, especialidade: p.especialidade })) }));
  }

  async listar(q: z.infer<typeof listaSchema>) {
    const termo = q.q ? `%${likeLiteral(q.q)}%` : undefined;
    const where = and(
      isNull(pessoas.excluido_em),
      termo ? or(ilike(pessoas.nome_razao, termo), ilike(pessoas.cpf_cnpj, termo), ilike(pessoas.email, termo), ilike(pessoas.telefone, termo)) : undefined,
      q.papel ? exists(this.db.select({ x: sql`1` }).from(pessoa_papeis).where(and(eq(pessoa_papeis.pessoa_id, pessoas.id), eq(pessoa_papeis.papel, q.papel)))) : undefined,
    );
    const [{ total }] = await this.db.select({ total: sql<number>`count(*)::int` }).from(pessoas).where(where);
    const rows = await this.db.select().from(pessoas).where(where).orderBy(asc(pessoas.nome_razao)).limit(q.pageSize).offset(offset(q));
    return paginado(await this.comPapeis(rows), total, q);
  }
  async obter(id: string) {
    const [r] = await this.db.select().from(pessoas).where(and(eq(pessoas.id, id), isNull(pessoas.excluido_em)));
    if (!r) throw new NotFoundException('Pessoa não encontrada');
    return (await this.comPapeis([r]))[0];
  }
  async criar(b: z.infer<typeof pessoaCriar>, u: AuthUser, ip: string) {
    const { papeis, ...dados } = b;
    const id = await this.db.transaction(async (tx) => {
      const [r] = await tx.insert(pessoas).values({ ...dados, criado_por: u.id }).returning({ id: pessoas.id });
      if (papeis.length) await tx.insert(pessoa_papeis).values(papeis.map((p) => ({ pessoa_id: r.id, papel: p.papel, especialidade: p.especialidade ?? null }))).onConflictDoNothing();
      return r.id;
    });
    const out = await this.obter(id);
    await this.audit.log({ usuario_id: u.id, entidade: 'pessoas', entidade_id: id, acao: 'CRIAR', depois: out, ip });
    return out;
  }
  async atualizar(id: string, b: z.infer<typeof pessoaAtualizar>, u: AuthUser, ip: string) {
    const antes = await this.obter(id);
    const { papeis, ...dados } = b;
    await this.db.transaction(async (tx) => {
      if (Object.keys(dados).length) await tx.update(pessoas).set(dados).where(eq(pessoas.id, id));
      if (papeis) {
        await tx.delete(pessoa_papeis).where(eq(pessoa_papeis.pessoa_id, id));
        if (papeis.length) await tx.insert(pessoa_papeis).values(papeis.map((p) => ({ pessoa_id: id, papel: p.papel, especialidade: p.especialidade ?? null }))).onConflictDoNothing();
      }
    });
    const depois = await this.obter(id);
    await this.audit.log({ usuario_id: u.id, entidade: 'pessoas', entidade_id: id, acao: 'ATUALIZAR', antes, depois, ip });
    return depois;
  }
  async excluir(id: string, u: AuthUser, ip: string) {
    const antes = await this.obter(id);
    await this.db.update(pessoas).set({ excluido_em: new Date(), cpf_cnpj: null }).where(eq(pessoas.id, id));
    await this.audit.log({ usuario_id: u.id, entidade: 'pessoas', entidade_id: id, acao: 'EXCLUIR', antes, ip });
  }
}

@Controller('pessoas')
class PessoasController {
  constructor(private readonly s: PessoasService) {}
  @Get() @RequirePermission('pessoas:ver') listar(@Query(new ZodPipe(listaSchema)) q: z.infer<typeof listaSchema>) { return this.s.listar(q); }
  @Post() @RequirePermission('pessoas:criar')
  criar(@Body(new ZodPipe(pessoaCriar)) b: z.infer<typeof pessoaCriar>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.criar(b, u, ip); }
  @Get(':id') @RequirePermission('pessoas:ver') obter(@Param('id', ParseUUIDPipe) id: string) { return this.s.obter(id); }
  @Patch(':id') @RequirePermission('pessoas:editar')
  atualizar(@Param('id', ParseUUIDPipe) id: string, @Body(new ZodPipe(pessoaAtualizar)) b: z.infer<typeof pessoaAtualizar>, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.atualizar(id, b, u, ip); }
  @Delete(':id') @HttpCode(204) @RequirePermission('pessoas:excluir')
  excluir(@Param('id', ParseUUIDPipe) id: string, @CurrentUser() u: AuthUser, @ClientIp() ip: string) { return this.s.excluir(id, u, ip); }
}

@Module({ controllers: [PessoasController], providers: [PessoasService], exports: [PessoasService] })
export class PessoasModule {}
