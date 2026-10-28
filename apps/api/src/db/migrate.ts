import { migrate } from 'drizzle-orm/node-postgres/migrator';
import { drizzle } from 'drizzle-orm/node-postgres';
import { Pool } from 'pg';
import { join } from 'node:path';

/** Executa as migrations pendentes. Usado no deploy (release command) e em dev. */
export async function runMigrations(url = process.env.DATABASE_URL) {
  if (!url) throw new Error('DATABASE_URL não definido');
  const pool = new Pool({ connectionString: url, ssl: url.includes('sslmode=require') ? { rejectUnauthorized: false } : undefined });
  try {
    await migrate(drizzle(pool), { migrationsFolder: join(__dirname, '../../drizzle') });
    console.log('Migrations aplicadas.');
  } finally {
    await pool.end();
  }
}

if (require.main === module) {
  runMigrations().catch((e) => { console.error(e); process.exit(1); });
}
