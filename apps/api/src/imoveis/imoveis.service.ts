import { Inject, Injectable, NotFoundException, ServiceUnavailableException, UnprocessableEntityException } from '@nestjs/common';
import { and, asc, desc, eq, ilike, isNull, or, sql } from 'drizzle-orm';
import { randomUUID } from 'node:crypto';
import { extname } from 'node:path';
import { z } from 'zod';
import { AuditService } from '../auditoria/audit.service';
import { StorageService } from '../arquivos/storage.service';
import type { AuthUser } from '../common/auth.types';
import { assertAcessoImovel } from '../common/escopo';
import { hojeBR } from '../common/datas';
import { offset, paginado } from '../common/pagination';
import { likeLiteral } from '../common/schemas';
import { DB, Database } from '../db/db.module';
import {
  imovel_caracteristicas, imovel_envolvidos, imovel_midias, imovel_proprietarios, imoveis, pessoas, regularizacoes, usuarios,
} from '../db/schema';
import * as S from './imoveis.schemas';

@Injectable()
export class ImoveisService {
  constructor(
    @Inject(DB) private readonly db: Database,
    private readonly audit: AuditService,
    private readonly storage: StorageService,
  ) {}

  /* ----------------------------- CRUD ----------------------------- */
  async listar(user: AuthUser, q: z.infer<typeof S.listaImoveisSchema>) {
    const termo = q.q ? `%${likeLiteral(q.q)}%` : undefined;
    const where = and(
      isNull(imoveis.excluido_em),
      user.escopo_proprio ? eq(imoveis.corretor_responsavel_id, user.id) : undefined,
      q.corretor_id && !user.escopo_proprio ? eq(imoveis.corretor_responsavel_id, q.corretor_id) : undefined,
      q.situacao ? eq(imoveis.situacao, q.situacao) : undefined,
      q.finalidade ? eq(imoveis.finalidade, q.finalidade) : undefined,
      q.tipo ? ilike(imoveis.tipo, q.tipo) : undefined,
      q.cidade ? ilike(imoveis.cidade, q.cidade) : undefined,
      termo ? or(ilike(imoveis.titulo, termo), ilike(imoveis.codigo, termo), ilike(imoveis.bairro, termo), ilike(imoveis.cidade, termo), ilike(imoveis.logradouro, termo), ilike(imoveis.matricula, termo)) : undefined,
    );
    const [{ total }] = await this.db.select({ total: sql<number>`count(*)::int` }).from(imoveis).where(where);
    const rows = await this.db
      .select({
        id: imoveis.id, codigo: imoveis.codigo, titulo: imoveis.titulo, tipo: imoveis.tipo, situacao: imoveis.situacao,
        finalidade: imoveis.finalidade, cidade: imoveis.cidade, bairro: imoveis.bairro, uf: imoveis.uf,
        preco_venda: imoveis.preco_venda, preco_locacao: imoveis.preco_locacao,
        corretor_id: usuarios.id, corretor_nome: usuarios.nome,
        capa_chave: sql<string | null>`(select m.arquivo_chave from imovel_midias m where m.imovel_id = ${imoveis.id} and m.tipo = 'FOTO' order by m.capa desc, m.ordem asc limit 1)`,
      })
      .from(imoveis).leftJoin(usuarios, eq(usuarios.id, imoveis.corretor_responsavel_id))
      .where(where).orderBy(desc(imoveis.criado_em)).limit(q.pageSize).offset(offset(q));
    const data = await Promise.all(rows.map(async ({ corretor_id, corretor_nome, capa_chave, ...r }) => ({
      ...r, corretor: corretor_id ? { id: corretor_id, nome: corretor_nome } : null, capa_url: await this.storage.urlAssinada(capa_chave),
    })));
    return paginado(data, total, q);
  }

  async detalhe(user: AuthUser, id: string) {
    const im = await assertAcessoImovel(this.db, user, id);
    const [carac, midias, props, envs, corretor] = await Promise.all([
      this.db.select().from(imovel_caracteristicas).where(eq(imovel_caracteristicas.imovel_id, id)).orderBy(asc(imovel_caracteristicas.chave)),
      this.db.select().from(imovel_midias).where(eq(imovel_midias.imovel_id, id)).orderBy(asc(imovel_midias.ordem)),
      this.db.select({ p: imovel_proprietarios, pessoa: { id: pessoas.id, nome_razao: pessoas.nome_razao, cpf_cnpj: pessoas.cpf_cnpj, telefone: pessoas.telefone, email: pessoas.email } })
        .from(imovel_proprietarios).innerJoin(pessoas, eq(pessoas.id, imovel_proprietarios.pessoa_id)).where(eq(imovel_proprietarios.imovel_id, id)),
      this.db.select({ e: imovel_envolvidos, pessoa: { id: pessoas.id, nome_razao: pessoas.nome_razao, telefone: pessoas.telefone, email: pessoas.email } })
        .from(imovel_envolvidos).innerJoin(pessoas, eq(pessoas.id, imovel_envolvidos.pessoa_id)).where(eq(imovel_envolvidos.imovel_id, id)),
      im.corretor_responsavel_id ? this.db.select({ id: usuarios.id, nome: usuarios.nome }).from(usuarios).where(eq(usuarios.id, im.corretor_responsavel_id)) : Promise.resolve([]),
    ]);
    return {
      ...im,
      caracteristicas: carac,
      midias: await Promise.all(midias.map(async (m) => ({ ...m, url: await this.storage.urlAssinada(m.arquivo_chave) }))),
      proprietarios: props.map((r) => ({ ...r.p, pessoa: r.pessoa })),
      envolvidos: envs.map((r) => ({ ...r.e, pessoa: r.pessoa })),
      corretor: corretor[0] ?? null,
    };
  }

  /** Código sequencial IMV-000123; lock consultivo evita duplicidade em criações simultâneas. */
  private async proximoCodigo(tx: Pick<Database, 'execute'>): Promise<string> {
    await tx.execute(sql`select pg_advisory_xact_lock(hashtext('imoveis_codigo'))`);
    const r = await tx.execute(sql`select coalesce(max(substring(codigo from 5)::int), 0) + 1 as n from imoveis where codigo ~ '^IMV-[0-9]+$'`);
    return `IMV-${String((r.rows[0] as { n: number }).n).padStart(6, '0')}`;
  }

  async criar(user: AuthUser, b: z.infer<typeof S.imovelCriar>, ip: string) {
    const id = await this.db.transaction(async (tx) => {
      const codigo = await this.proximoCodigo(tx);
      const corretor = user.escopo_proprio ? user.id : (b.corretor_responsavel_id ?? user.id);
      const [r] = await tx.insert(imoveis).values({ ...b, codigo, corretor_responsavel_id: corretor }).returning({ id: imoveis.id });
      return r.id;
    });
    const out = await this.detalhe(user, id);
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'CRIAR', depois: out, ip });
    return out;
  }

  async atualizar(user: AuthUser, id: string, b: z.infer<typeof S.imovelAtualizar>, ip: string) {
    const antes = await this.detalhe(user, id);
    const dados = { ...b };
    if (user.escopo_proprio) delete dados.corretor_responsavel_id; // corretor não repassa o imóvel
    if (Object.keys(dados).length) await this.db.update(imoveis).set(dados).where(eq(imoveis.id, id));
    const depois = await this.detalhe(user, id);
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'ATUALIZAR', antes, depois, ip });
    return depois;
  }

  async excluir(user: AuthUser, id: string, ip: string) {
    const antes = await assertAcessoImovel(this.db, user, id);
    await this.db.update(imoveis).set({ excluido_em: new Date() }).where(eq(imoveis.id, id));
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'EXCLUIR', antes, ip });
  }

  /* ----------------------- Conjuntos (substituição) ----------------------- */
  async setCaracteristicas(user: AuthUser, id: string, itens: z.infer<typeof S.caracteristicasSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, id);
    await this.db.transaction(async (tx) => {
      await tx.delete(imovel_caracteristicas).where(eq(imovel_caracteristicas.imovel_id, id));
      const unicos = [...new Map(itens.map((i) => [i.chave.toLowerCase(), i])).values()];
      if (unicos.length) await tx.insert(imovel_caracteristicas).values(unicos.map((i) => ({ imovel_id: id, chave: i.chave, valor: i.valor ?? null, publico: i.publico })));
    });
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'ATUALIZAR', depois: { caracteristicas: itens }, ip });
    return this.db.select().from(imovel_caracteristicas).where(eq(imovel_caracteristicas.imovel_id, id)).orderBy(asc(imovel_caracteristicas.chave));
  }

  /** Regra: a soma dos percentuais VIGENTES (hoje) dos proprietários deve ser 100%. */
  async setProprietarios(user: AuthUser, id: string, itens: z.infer<typeof S.proprietariosSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, id);
    const hoje = hojeBR();
    const vigentes = itens.filter((i) => (!i.inicio_em || i.inicio_em <= hoje) && (!i.fim_em || i.fim_em >= hoje));
    const soma = vigentes.reduce((a, i) => a + Math.round(Number(i.percentual) * 100), 0);
    if (itens.length && soma !== 10000) {
      throw new UnprocessableEntityException({ errors: { proprietarios: [`A soma dos percentuais vigentes deve ser 100% (atual: ${(soma / 100).toFixed(2)}%)`] } });
    }
    await this.db.transaction(async (tx) => {
      await tx.delete(imovel_proprietarios).where(eq(imovel_proprietarios.imovel_id, id));
      if (itens.length) await tx.insert(imovel_proprietarios).values(itens.map((i) => ({ imovel_id: id, pessoa_id: i.pessoa_id, percentual: i.percentual, principal: i.principal, inicio_em: i.inicio_em ?? null, fim_em: i.fim_em ?? null })));
    });
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'ATUALIZAR', depois: { proprietarios: itens }, ip });
    return (await this.detalhe(user, id)).proprietarios;
  }

  async setEnvolvidos(user: AuthUser, id: string, itens: z.infer<typeof S.envolvidosSchema>, ip: string) {
    await assertAcessoImovel(this.db, user, id);
    await this.db.transaction(async (tx) => {
      await tx.delete(imovel_envolvidos).where(eq(imovel_envolvidos.imovel_id, id));
      if (itens.length) await tx.insert(imovel_envolvidos).values(itens.map((i) => ({ imovel_id: id, pessoa_id: i.pessoa_id, papel: i.papel, observacao: i.observacao ?? null }))).onConflictDoNothing();
    });
    await this.audit.log({ usuario_id: user.id, entidade: 'imoveis', entidade_id: id, acao: 'ATUALIZAR', depois: { envolvidos: itens }, ip });
    return (await this.detalhe(user, id)).envolvidos;
  }

  /* ------------------------------ Mídias ------------------------------ */
  async enviarMidia(user: AuthUser, id: string, arquivo: { buffer: Buffer; originalname: string; mimetype: string } | undefined, b: z.infer<typeof S.midiaUploadSchema>) {
    await assertAcessoImovel(this.db, user, id);
    if (!arquivo) throw new UnprocessableEntityException({ errors: { arquivo: ['Envie um arquivo'] } });
    if (!this.storage.habilitado) throw new ServiceUnavailableException('Armazenamento de arquivos não configurado (S3_*).');
    const chave = `imoveis/${id}/${randomUUID()}${extname(arquivo.originalname).toLowerCase().slice(0, 8)}`;
    await this.storage.enviar(chave, arquivo.buffer, arquivo.mimetype);
    const [{ n, fotos }] = await this.db.select({ n: sql<number>`coalesce(max(${imovel_midias.ordem}), -1) + 1`, fotos: sql<number>`count(*) filter (where ${imovel_midias.tipo} = 'FOTO')::int` })
      .from(imovel_midias).where(eq(imovel_midias.imovel_id, id));
    const [m] = await this.db.insert(imovel_midias).values({
      imovel_id: id, tipo: b.tipo, arquivo_chave: chave, nome_original: arquivo.originalname.slice(0, 255), legenda: b.legenda ?? null,
      ordem: Number(n), capa: b.tipo === 'FOTO' && Number(fotos) === 0, publica: b.publica,
    }).returning();
    return { ...m, url: await this.storage.urlAssinada(chave) };
  }

  private async midiaComAcesso(user: AuthUser, midiaId: string) {
    const [m] = await this.db.select().from(imovel_midias).where(eq(imovel_midias.id, midiaId));
    if (!m) throw new NotFoundException('Mídia não encontrada');
    await assertAcessoImovel(this.db, user, m.imovel_id);
    return m;
  }
  async atualizarMidia(user: AuthUser, midiaId: string, b: z.infer<typeof S.midiaPatchSchema>) {
    const m = await this.midiaComAcesso(user, midiaId);
    await this.db.transaction(async (tx) => {
      if (b.capa) await tx.update(imovel_midias).set({ capa: false }).where(eq(imovel_midias.imovel_id, m.imovel_id));
      await tx.update(imovel_midias).set({ legenda: b.legenda ?? undefined, ordem: b.ordem, capa: b.capa, publica: b.publica }).where(eq(imovel_midias.id, midiaId));
    });
    const [r] = await this.db.select().from(imovel_midias).where(eq(imovel_midias.id, midiaId));
    return { ...r, url: await this.storage.urlAssinada(r.arquivo_chave) };
  }
  async removerMidia(user: AuthUser, midiaId: string) {
    const m = await this.midiaComAcesso(user, midiaId);
    await this.db.delete(imovel_midias).where(eq(imovel_midias.id, midiaId));
    await this.storage.remover(m.arquivo_chave);
  }

  /* --------------------------- Regularizações --------------------------- */
  async listarRegularizacoes(user: AuthUser, id: string) {
    await assertAcessoImovel(this.db, user, id);
    const rows = await this.db.select({ r: regularizacoes, prof: { id: pessoas.id, nome_razao: pessoas.nome_razao } })
      .from(regularizacoes).leftJoin(pessoas, eq(pessoas.id, regularizacoes.profissional_id))
      .where(and(eq(regularizacoes.imovel_id, id), isNull(regularizacoes.excluido_em))).orderBy(desc(regularizacoes.criado_em));
    return rows.map((x) => ({ ...x.r, profissional: x.prof?.id ? x.prof : null }));
  }
  async criarRegularizacao(user: AuthUser, id: string, b: z.infer<typeof S.regularizacaoCriar>, ip: string) {
    await assertAcessoImovel(this.db, user, id);
    const [r] = await this.db.insert(regularizacoes).values({ ...b, imovel_id: id }).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'regularizacoes', entidade_id: r.id, acao: 'CRIAR', depois: r, ip });
    return r;
  }
  private async regComAcesso(user: AuthUser, regId: string) {
    const [r] = await this.db.select().from(regularizacoes).where(and(eq(regularizacoes.id, regId), isNull(regularizacoes.excluido_em)));
    if (!r) throw new NotFoundException('Regularização não encontrada');
    await assertAcessoImovel(this.db, user, r.imovel_id);
    return r;
  }
  async atualizarRegularizacao(user: AuthUser, regId: string, b: z.infer<typeof S.regularizacaoAtualizar>, ip: string) {
    const antes = await this.regComAcesso(user, regId);
    const [r] = await this.db.update(regularizacoes).set(b).where(eq(regularizacoes.id, regId)).returning();
    await this.audit.log({ usuario_id: user.id, entidade: 'regularizacoes', entidade_id: regId, acao: 'ATUALIZAR', antes, depois: r, ip });
    return r;
  }
  async excluirRegularizacao(user: AuthUser, regId: string, ip: string) {
    const antes = await this.regComAcesso(user, regId);
    await this.db.update(regularizacoes).set({ excluido_em: new Date() }).where(eq(regularizacoes.id, regId));
    await this.audit.log({ usuario_id: user.id, entidade: 'regularizacoes', entidade_id: regId, acao: 'EXCLUIR', antes, ip });
  }
}
