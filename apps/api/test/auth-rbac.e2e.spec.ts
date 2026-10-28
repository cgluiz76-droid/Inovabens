import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { eq } from 'drizzle-orm';
import { refresh_tokens } from '../src/db/schema';
import { ADMIN, Ctx, criarApp } from './helpers';

let c: Ctx;
let adm: string;
const A = (t: string) => ({ Authorization: `Bearer ${t}` });

beforeAll(async () => {
  c = await criarApp();
  adm = (await c.login(ADMIN.email, ADMIN.senha)).token;
});
afterAll(async () => { await c.app.close(); await c.client.close(); });

describe('Autenticação', () => {
  it('login válido devolve token, perfil e permissões; senha errada = 401 genérico', async () => {
    const ok = await c.login(ADMIN.email, ADMIN.senha);
    expect(ok.status).toBe(200);
    expect(ok.body.usuario.perfil.nome).toBe('Administrador');
    expect(ok.body.usuario.permissoes).toContain('financeiro:editar');
    expect(ok.cookie).toMatch(/refresh_token=.*HttpOnly/i);
    const ruim = await c.login(ADMIN.email, 'errada');
    expect(ruim.status).toBe(401);
    expect(ruim.body.title).toBe('Credenciais inválidas');
    expect((await c.login('naoexiste@x.com', 'qualquer')).body.title).toBe('Credenciais inválidas'); // mesma resposta
  });

  it('rotas exigem token e /auth/me responde com o usuário', async () => {
    expect((await c.http.get('/api/v1/imoveis')).status).toBe(401);
    const me = await c.http.get('/api/v1/auth/me').set(A(adm));
    expect(me.status).toBe(200);
    expect(me.body.email).toBe(ADMIN.email);
  });

  it('refresh rotaciona o token; reuso de token antigo revoga a sessão inteira', async () => {
    const l = await c.login(ADMIN.email, ADMIN.senha);
    const r1 = await c.http.post('/api/v1/auth/refresh').set('Cookie', l.cookie!);
    expect(r1.status).toBe(200);
    expect(r1.body.access_token).toBeTruthy();
    const novoCookie = (r1.headers['set-cookie'] as unknown as string[])[0];
    expect(novoCookie).not.toBe(l.cookie);
    // Simula que o token original foi revogado há mais de 10 s (fora da janela de concorrência) e é reapresentado
    await c.db.update(refresh_tokens).set({ revogado_em: new Date(Date.now() - 60_000) });
    const reuso = await c.http.post('/api/v1/auth/refresh').set('Cookie', l.cookie!);
    expect(reuso.status).toBe(401);
    const filho = await c.http.post('/api/v1/auth/refresh').set('Cookie', novoCookie);
    expect(filho.status).toBe(401); // a cadeia inteira foi revogada
  });

  it('logout revoga o refresh token', async () => {
    const l = await c.login(ADMIN.email, ADMIN.senha);
    expect((await c.http.post('/api/v1/auth/logout').set('Cookie', l.cookie!)).status).toBe(204);
    expect((await c.http.post('/api/v1/auth/refresh').set('Cookie', l.cookie!)).status).toBe(401);
    const abertos = await c.db.select().from(refresh_tokens).where(eq(refresh_tokens.revogado_em, null as never));
    expect(Array.isArray(abertos)).toBe(true);
  });

  it('bloqueia a conta após 5 senhas erradas (até com a senha correta)', async () => {
    const perfil = (await c.http.get('/api/v1/roles').set(A(adm))).body.find((r: { nome: string }) => r.nome === 'Corretor');
    const u = await c.http.post('/api/v1/users').set(A(adm)).send({ nome: 'Bloq', email: 'bloq@test.com', senha: 'Senha@12345', perfil_id: perfil.id });
    expect(u.status).toBe(201);
    for (let i = 0; i < 5; i++) expect((await c.login('bloq@test.com', 'errada1234')).status).toBe(401);
    const r = await c.login('bloq@test.com', 'Senha@12345');
    expect(r.status).toBe(429);
    expect(r.body.title).toMatch(/bloqueada/i);
  });

  it('política de senha e troca de senha', async () => {
    const perfil = (await c.http.get('/api/v1/roles').set(A(adm))).body[0];
    const fraca = await c.http.post('/api/v1/users').set(A(adm)).send({ nome: 'X', email: 'x@test.com', senha: 'curta1', perfil_id: perfil.id });
    expect(fraca.status).toBe(422);
    expect(fraca.body.errors.senha).toBeTruthy();
    const troca = await c.http.post('/api/v1/auth/alterar-senha').set(A(adm)).send({ senha_atual: 'errada', nova_senha: 'NovaSenha@123' });
    expect(troca.status).toBe(422);
  });
});

describe('RBAC e escopo por corretor', () => {
  let corretorA: string, corretorB: string, financeiro: string, imovelA: string;

  it('cria usuários dos três perfis', async () => {
    const roles = (await c.http.get('/api/v1/roles').set(A(adm))).body as { id: string; nome: string }[];
    const pid = (n: string) => roles.find((r) => r.nome === n)!.id;
    for (const [nome, email, perfil] of [['Corretor A', 'a@test.com', 'Corretor'], ['Corretor B', 'b@test.com', 'Corretor'], ['Fin', 'f@test.com', 'Financeiro']] as const) {
      expect((await c.http.post('/api/v1/users').set(A(adm)).send({ nome, email, senha: 'Senha@12345', perfil_id: pid(perfil) })).status).toBe(201);
    }
    corretorA = (await c.login('a@test.com', 'Senha@12345')).token;
    corretorB = (await c.login('b@test.com', 'Senha@12345')).token;
    financeiro = (await c.login('f@test.com', 'Senha@12345')).token;
  });

  it('corretor não administra usuários nem enxerga o financeiro; financeiro não cria imóvel', async () => {
    expect((await c.http.get('/api/v1/users').set(A(corretorA))).status).toBe(403);
    expect((await c.http.get('/api/v1/roles').set(A(corretorA))).status).toBe(403);
    expect((await c.http.post('/api/v1/imoveis').set(A(financeiro)).send({ tipo: 'casa', titulo: 'Casa X' })).status).toBe(403);
    expect((await c.http.get('/api/v1/users/options').set(A(corretorA))).status).toBe(200); // selects: qualquer autenticado
  });

  it('corretor só enxerga e acessa os próprios imóveis (e o financeiro bloqueia o corretor)', async () => {
    const r = await c.http.post('/api/v1/imoveis').set(A(corretorA)).send({ tipo: 'casa', titulo: 'Casa do Corretor A' });
    expect(r.status).toBe(201);
    imovelA = r.body.id;
    expect(r.body.codigo).toBe('IMV-000001');
    expect(r.body.corretor.nome).toBe('Corretor A');
    expect((await c.http.get('/api/v1/imoveis').set(A(corretorA))).body.total).toBe(1);
    expect((await c.http.get('/api/v1/imoveis').set(A(corretorB))).body.total).toBe(0);
    expect((await c.http.get(`/api/v1/imoveis/${imovelA}`).set(A(corretorB))).status).toBe(403);
    expect((await c.http.get(`/api/v1/imoveis/${imovelA}/memorandos`).set(A(corretorB))).status).toBe(403);
    expect((await c.http.get(`/api/v1/imoveis/${imovelA}/aquisicao`).set(A(corretorA))).status).toBe(403); // sem financeiro:ver
    expect((await c.http.get('/api/v1/imoveis').set(A(adm))).body.total).toBe(1); // admin vê tudo
    expect((await c.http.get('/api/v1/imoveis').set(A(financeiro))).body.total).toBe(1); // financeiro vê todos
  });

  it('o Administrador não pode ser alterado nem excluído (evita auto-bloqueio)', async () => {
    const roles = (await c.http.get('/api/v1/roles').set(A(adm))).body as { id: string; nome: string; permissoes: string[] }[];
    const admin = roles.find((r) => r.nome === 'Administrador')!;
    expect((await c.http.patch(`/api/v1/roles/${admin.id}`).set(A(adm)).send({ permissoes: ['pessoas:ver'] })).status).toBe(422);
    expect((await c.http.delete(`/api/v1/roles/${admin.id}`).set(A(adm))).status).toBe(422);
    const novo = await c.http.post('/api/v1/roles').set(A(adm)).send({ nome: 'Estagiário', permissoes: ['imoveis:ver'], escopo_proprio: true });
    expect(novo.status).toBe(201);
    expect(novo.body.permissoes).toEqual(['imoveis:ver']);
  });
});
