// Variáveis de ambiente ANTES de qualquer import da aplicação (env() lê e faz cache na 1ª chamada)
Object.assign(process.env, {
  NODE_ENV: 'test', DATABASE_URL: 'postgres://teste:teste@localhost:5432/teste', DISABLE_JOBS: 'true',
  JWT_ACCESS_SECRET: 'a'.repeat(40), JWT_REFRESH_SECRET: 'b'.repeat(40), TOKEN_ENC_KEY: 'c1'.repeat(32),
  GOOGLE_CLIENT_ID: 'cid', GOOGLE_CLIENT_SECRET: 'csec', GOOGLE_REDIRECT_URI: 'http://localhost:3000/api/v1/integracoes/google/callback',
});
import { Type } from '@nestjs/common';
import { NestExpressApplication } from '@nestjs/platform-express';
import { Test } from '@nestjs/testing';
import cookieParser from 'cookie-parser';
import request from 'supertest';
import { AppModule } from '../src/app.module';
import { ProblemFilter } from '../src/common/problem.filter';
import { DB } from '../src/db/db.module';
import { seedAdmin, seedBase } from '../src/db/seed';
import { createTestDb } from './pglite';

export const ADMIN = { email: 'admin@test.com', senha: 'Senha@12345' };

export async function criarApp() {
  const t = await createTestDb();
  await seedBase(t.db as never);
  await seedAdmin(t.db as never, ADMIN.email, ADMIN.senha);
  const mod = await Test.createTestingModule({ imports: [AppModule] })
    .overrideProvider(DB).useValue(t.db)
    .compile();
  const app = mod.createNestApplication<NestExpressApplication>();
  app.use(cookieParser());
  app.setGlobalPrefix('api/v1');
  app.useGlobalFilters(new ProblemFilter());
  await app.init();
  const http = request(app.getHttpServer());
  const get = <T>(c: Type<T>) => app.get(c);
  /** Faz login e devolve `Authorization` pronto + cookies de refresh. */
  const login = async (email: string, senha: string) => {
    const r = await http.post('/api/v1/auth/login').send({ email, senha });
    return { status: r.status, body: r.body, token: r.body.access_token as string, cookie: (r.headers['set-cookie'] as unknown as string[] | undefined)?.[0] };
  };
  const binario = (r: request.Test) => r.buffer(true).parse((res, cb) => { const p: Buffer[] = []; res.on('data', (c: Buffer) => p.push(c)); res.on('end', () => cb(null, Buffer.concat(p))); });
  return { app, http, db: t.db, client: t.client, get, login, binario };
}
export type Ctx = Awaited<ReturnType<typeof criarApp>>;
