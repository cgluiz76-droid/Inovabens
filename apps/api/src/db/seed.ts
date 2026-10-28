import { eq, sql } from 'drizzle-orm';
import { hash } from '@node-rs/argon2';
import { drizzle } from 'drizzle-orm/node-postgres';
import { Pool } from 'pg';
import { CATEGORIAS_DESPESA_PADRAO, ESTAGIOS_PADRAO, FUNIL, PERFIS_PADRAO, PERMISSOES } from '@inovabens/shared';
import * as schema from './schema';
import type { Database } from './db.module';

/**
 * Dados-base idempotentes: permissões, perfis padrão (só ADICIONA permissões, nunca remove as
 * customizações do admin), categorias de despesa e estágios dos dois funis.
 */
export async function seedBase(db: Database) {
  const s = schema;
  await db.insert(s.permissoes).values(PERMISSOES.map((codigo) => ({ codigo, descricao: codigo }))).onConflictDoNothing();
  const perms = await db.select().from(s.permissoes);
  const idPerm = new Map(perms.map((p) => [p.codigo, p.id]));

  for (const p of PERFIS_PADRAO) {
    await db.insert(s.perfis).values({ nome: p.nome, descricao: p.descricao, escopo_proprio: p.escopo_proprio }).onConflictDoNothing();
    const [perfil] = await db.select().from(s.perfis).where(eq(s.perfis.nome, p.nome));
    await db.insert(s.perfil_permissoes)
      .values(p.permissoes.map((c) => ({ perfil_id: perfil.id, permissao_id: idPerm.get(c)! })))
      .onConflictDoNothing();
  }
  await db.insert(s.despesa_categorias).values(CATEGORIAS_DESPESA_PADRAO.map((nome) => ({ nome }))).onConflictDoNothing();
  for (const funil of FUNIL) {
    await db.insert(s.funil_estagios)
      .values(ESTAGIOS_PADRAO.map((e, i) => ({ funil, nome: e.nome, tipo: e.tipo, cor: e.cor, ordem: i + 1 })))
      .onConflictDoNothing();
  }
}

/** Cria o administrador inicial SOMENTE se ainda não existe nenhum usuário. Retorna true se criou. */
export async function seedAdmin(db: Database, email: string, senha: string): Promise<boolean> {
  const [{ n }] = await db.select({ n: sql<number>`count(*)::int` }).from(schema.usuarios);
  if (n > 0) return false;
  const [perfil] = await db.select().from(schema.perfis).where(eq(schema.perfis.nome, 'Administrador'));
  await db.insert(schema.usuarios).values({ nome: 'Administrador', email: email.toLowerCase(), senha_hash: await hash(senha), perfil_id: perfil.id });
  return true;
}

export async function seed(url = process.env.DATABASE_URL) {
  if (!url) throw new Error('DATABASE_URL não definido');
  const email = process.env.SEED_ADMIN_EMAIL ?? 'admin@inovabens.com.br';
  const senha = process.env.SEED_ADMIN_PASSWORD ?? 'Trocar@123456';
  if (process.env.NODE_ENV === 'production' && senha === 'Trocar@123456') {
    throw new Error('Defina SEED_ADMIN_PASSWORD (não use a senha padrão em produção).');
  }
  const pool = new Pool({ connectionString: url, ssl: url.includes('sslmode=require') ? { rejectUnauthorized: false } : undefined });
  try {
    const db = drizzle(pool, { schema }) as unknown as Database;
    await seedBase(db);
    const criou = await seedAdmin(db, email, senha);
    console.log(`Seed ok.${criou ? ` Administrador criado: ${email}` : ' (administrador já existia)'}`);
  } finally {
    await pool.end();
  }
}

if (require.main === module) {
  seed().catch((e) => { console.error(e); process.exit(1); });
}
