import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { eq, sql } from 'drizzle-orm';
import { createTestDb } from './pglite';

let t: Awaited<ReturnType<typeof createTestDb>>;
let imovelId: string;
let userId: string;

beforeAll(async () => {
  t = await createTestDb();
  const { db, schema: s } = t;
  const [perfil] = await db.insert(s.perfis).values({ nome: 'Administrador' }).returning();
  const [u] = await db.insert(s.usuarios).values({ nome: 'Admin', email: 'a@b.com', senha_hash: 'x', perfil_id: perfil.id }).returning();
  userId = u.id;
  const [im] = await db.insert(s.imoveis).values({ codigo: 'IMV-000001', tipo: 'casa', titulo: 'Casa teste' }).returning();
  imovelId = im.id;
});
afterAll(async () => { await t.client.close(); });

describe('Regras de negócio no banco', () => {
  it('Total de Aquisição = Valor Pago + Impostos + Taxas + Despesas', async () => {
    const { db, schema: s } = t;
    const [aq] = await db.insert(s.aquisicoes).values({
      imovel_id: imovelId, data_aquisicao: '2026-01-10',
      valor_pago: '300000.00', impostos: '9000.00', taxas: '2500.50', despesas_diversas: '1000.25',
    }).returning();
    expect(aq.total_aquisicao).toBe('312500.75');
  });

  it('itens de aquisição recalculam impostos/taxas/despesas e o total', async () => {
    const { db, schema: s } = t;
    const [aq] = await db.select().from(s.aquisicoes).where(eq(s.aquisicoes.imovel_id, imovelId));
    await db.insert(s.aquisicao_itens).values([
      { aquisicao_id: aq.id, tipo: 'IMPOSTO', descricao: 'ITBI', valor: '6000.00' },
      { aquisicao_id: aq.id, tipo: 'IMPOSTO', descricao: 'Outro imposto', valor: '500.00' },
      { aquisicao_id: aq.id, tipo: 'TAXA', descricao: 'Registro', valor: '1200.00' },
      { aquisicao_id: aq.id, tipo: 'DESPESA', descricao: 'Certidões', valor: '300.00' },
    ]);
    const [r] = await db.select().from(s.aquisicoes).where(eq(s.aquisicoes.id, aq.id));
    expect(r.impostos).toBe('6500.00');
    expect(r.taxas).toBe('1200.00');
    expect(r.despesas_diversas).toBe('300.00');
    expect(r.total_aquisicao).toBe('308000.00'); // 300000 + 6500 + 1200 + 300
  });

  it('não permite gravar total_aquisicao (coluna gerada)', async () => {
    const { db } = t;
    await expect(db.execute(sql`UPDATE aquisicoes SET total_aquisicao = 1`)).rejects.toThrow();
  });

  it('NAO_PAGO sem justificativa é rejeitado; com justificativa é aceito', async () => {
    const { db, schema: s } = t;
    const [cat] = await db.insert(s.despesa_categorias).values({ nome: 'IPTU' }).returning();
    const [d] = await db.insert(s.despesas).values({
      imovel_id: imovelId, categoria_id: cat.id, descricao: 'IPTU 2026', recorrencia: 'MENSAL', inicio_em: '2026-01-01',
    }).returning();
    await expect(db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-02-01', status: 'NAO_PAGO' })).rejects.toThrow();
    await expect(db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-02-01', status: 'NAO_PAGO', justificativa: '   curto ' })).rejects.toThrow();
    await db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-02-01', status: 'NAO_PAGO', justificativa: 'Aguardando segunda via do boleto' });
    // status em branco (a marcar), PAGO e NAO_SE_APLICA não exigem justificativa
    await db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-03-01' });
    await db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-04-01', status: 'PAGO', data_pagamento: '2026-04-05' });
    await db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-05-01', status: 'NAO_SE_APLICA' });
  });

  it('uma competência por despesa e competência sempre no dia 1', async () => {
    const { db, schema: s } = t;
    const [d] = await db.select().from(s.despesas).limit(1);
    await expect(db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-02-01' })).rejects.toThrow();
    await expect(db.insert(s.despesa_lancamentos).values({ despesa_id: d.id, competencia: '2026-06-15' })).rejects.toThrow();
  });

  it('um alerta aberto por compromisso', async () => {
    const { db, schema: s } = t;
    const [c] = await db.insert(s.compromissos).values({
      imovel_id: imovelId, titulo: 'Visita', tipo: 'VISITA', responsavel_id: userId,
      inicio_em: new Date('2026-01-01T10:00:00Z'), fim_em: new Date('2026-01-01T11:00:00Z'),
    }).returning();
    await db.insert(s.alertas).values({ tipo: 'COMPROMISSO_NAO_CUMPRIDO', usuario_id: userId, compromisso_id: c.id, mensagem: 'x' });
    await expect(db.insert(s.alertas).values({ tipo: 'COMPROMISSO_NAO_CUMPRIDO', usuario_id: userId, compromisso_id: c.id, mensagem: 'y' })).rejects.toThrow();
    await db.update(s.alertas).set({ resolvido_em: new Date() }).where(eq(s.alertas.compromisso_id, c.id));
    await db.insert(s.alertas).values({ tipo: 'COMPROMISSO_NAO_CUMPRIDO', usuario_id: userId, compromisso_id: c.id, mensagem: 'z' });
  });

  it('audit_log é somente inserção', async () => {
    const { db, schema: s } = t;
    const [a] = await db.insert(s.audit_log).values({ entidade: 'imoveis', acao: 'CRIAR' }).returning();
    await expect(db.execute(sql`UPDATE audit_log SET acao = 'X' WHERE id = ${a.id}`)).rejects.toThrow();
    await expect(db.execute(sql`DELETE FROM audit_log WHERE id = ${a.id}`)).rejects.toThrow();
  });
});
