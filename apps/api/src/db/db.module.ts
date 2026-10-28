import { Global, Inject, Module, OnApplicationShutdown } from '@nestjs/common';
import { drizzle, NodePgDatabase } from 'drizzle-orm/node-postgres';
import { Pool } from 'pg';
import * as schema from './schema';
import { env } from '../config/env';

export const DB = Symbol('DB');
const POOL = Symbol('PG_POOL');
export type Database = NodePgDatabase<typeof schema>;
/** Transação ou o próprio banco — aceito pelos helpers. */
export type Tx = Parameters<Parameters<Database['transaction']>[0]>[0];
export { schema };

@Global()
@Module({
  providers: [
    {
      provide: POOL,
      useFactory: () => {
        const url = env().DATABASE_URL;
        return new Pool({ connectionString: url, max: 10, ssl: url.includes('sslmode=require') ? { rejectUnauthorized: false } : undefined });
      },
    },
    { provide: DB, useFactory: (pool: Pool) => drizzle(pool, { schema }), inject: [POOL] },
  ],
  exports: [DB],
})
export class DbModule implements OnApplicationShutdown {
  constructor(@Inject(POOL) private readonly pool: Pool) {}
  async onApplicationShutdown() { await this.pool.end(); }
}
