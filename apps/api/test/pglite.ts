import { PGlite } from '@electric-sql/pglite';
import { drizzle } from 'drizzle-orm/pglite';
import { migrate } from 'drizzle-orm/pglite/migrator';
import { join } from 'node:path';
import * as schema from '../src/db/schema';

/** Banco PostgreSQL em memória (PGlite) com TODAS as migrations aplicadas. Para testes. */
export async function createTestDb() {
  const client = new PGlite();
  const db = drizzle(client, { schema });
  await migrate(db, { migrationsFolder: join(__dirname, '../drizzle') });
  return { db, client, schema };
}
