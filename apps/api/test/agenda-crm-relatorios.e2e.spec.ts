import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { codigoImovelNoTexto, deEventoGoogle, paraEventoGoogle } from '../src/agenda/google.service';
import { AlertasService } from '../src/alertas/alertas.module';
import { ADMIN, Ctx, criarApp } from './helpers';

let c: Ctx;
let adm: string, cor: string, fin: string;
const H = (t: string) => ({ Authorization: `Bearer ${t}` });
const call = (m: 'get' | 'post' | 'put' | 'patch' | 'delete', t: string, url: string, body?: object) => { const r = c.http[m](`/api/v1${url}`).set(H(t)); return body ? r.send(body) : r; };
const A = (m: 'get' | 'post' | 'put' | 'patch' | 'delete', url: string, body?: object) => call(m, adm, url, body);
const horas = (h: number) => new Date(Date.now() + h * 3600_000).toISOString();

beforeAll(async () => {
  c = await criarApp();
  adm = (await c.login(ADMIN.email, ADMIN.senha)).token;
  const roles = (await A('get', '/roles')).body as { id: string; nome: string }[];
  for (const [nome, email, perfil] of [['Cor', 'cor@test.com', 'Corretor'], ['Fin', 'fin@test.com', 'Financeiro']] as const)
    await A('post', '/users', { nome, email, senha: 'Senha@12345', perfil_id: roles.find((r) => r.nome === perfil)!.id });
  cor = (await c.login('cor@test.com', 'Senha@12345')).token;
  fin = (await c.login('fin@test.com', 'Senha@12345')).token;
});
afterAll(async () => { await c.app.close(); await c.client.close(); });

describe('Agenda e alertas de compromissos não cumpridos', () => {
  let imv: string, vencido: string, futuro: string;
  it('cria compromissos e valida o período', async () => {
    imv = (await A('post', '/imoveis', { tipo: 'casa', titulo: 'Casa da agenda' })).body.id;
    const ruim = await A('post', `/imoveis/${imv}/compromissos`, { titulo: 'Visita', inicio_em: horas(2), fim_em: horas(1) });
    expect(ruim.status).toBe(422);
    const r = await A('post', `/imoveis/${imv}/compromissos`, { tipo: 'VISITA', titulo: 'Visita ao imóvel', inicio_em: horas(-5), fim_em: horas(-4), participantes: [{ email: 'CLIENTE@x.com' }] });
    expect(r.status).toBe(201);
    vencido = r.body.id;
    expect(r.body.status).toBe('PENDENTE');
    futuro = (await A('post', `/imoveis/${imv}/compromissos`, { titulo: 'Reunião futura', inicio_em: horas(24), fim_em: horas(25) })).body.id;
    expect((await A('get', `/imoveis/${imv}/compromissos`)).body).toHaveLength(2);
  });

  it('REGRA: compromisso pendente após o término gera 1 alerta (idempotente) e some ao concluir', async () => {
    const svc = c.get(AlertasService);
    expect((await svc.verificarCompromissos()).criados).toBe(1);
    expect((await svc.verificarCompromissos()).criados).toBe(0);
    const lista = (await A('get', '/alertas')).body as { tipo: string; mensagem: string }[];
    expect(lista).toHaveLength(1);
    expect(lista[0].tipo).toBe('COMPROMISSO_NAO_CUMPRIDO');
    expect(lista[0].mensagem).toMatch(/Visita ao imóvel \(IMV-/);
    expect((await A('get', '/alertas/contagem')).body.abertos).toBe(1);
    expect((await call('get', cor, '/alertas')).body).toHaveLength(0); // alerta é do responsável
    const r = await A('post', `/compromissos/${vencido}/concluir`);
    expect(r.status).toBe(200);
    expect(r.body.status).toBe('CONCLUIDO');
    expect((await A('get', '/alertas')).body).toHaveLength(0);
  });

  it('reagendar a partir do alerta resolve e cria novo ciclo', async () => {
    const svc = c.get(AlertasService);
    await A('patch', `/compromissos/${futuro}`, { inicio_em: horas(-3), fim_em: horas(-2) });
    expect((await svc.verificarCompromissos()).criados).toBe(1);
    const alerta = (await A('get', '/alertas')).body[0];
    const re = await A('patch', `/alertas/${alerta.id}/reagendar`, { inicio_em: horas(48), fim_em: horas(49) });
    expect(re.status).toBe(200);
    expect((await A('get', '/alertas')).body).toHaveLength(0);
    expect((await A('get', `/compromissos/${futuro}`)).body.status).toBe('PENDENTE');
  });

  it('corretor só mexe na agenda dos próprios imóveis', async () => {
    expect((await call('get', cor, `/imoveis/${imv}/compromissos`)).status).toBe(403);
    expect((await call('get', cor, '/compromissos')).body).toHaveLength(0);
  });

  it('alerta de despesa vencida vai para quem vê o financeiro e some ao pagar', async () => {
    const svc = c.get(AlertasService);
    const cat = (await A('get', '/categorias-despesa')).body[0].id;
    await A('post', `/imoveis/${imv}/despesas`, { categoria_id: cat, descricao: 'Condomínio', recorrencia: 'UNICA', inicio_em: '2025-01-01', dia_vencimento: 5, valor_previsto: '700' });
    const l = (await A('get', `/imoveis/${imv}/despesas/checklist?mes=2025-01`)).body.itens[0].lancamento_id;
    const r = await svc.verificarDespesas();
    expect(r.criados).toBeGreaterThanOrEqual(2); // admin + financeiro
    expect((await call('get', fin, '/alertas')).body.some((a: { tipo: string }) => a.tipo === 'DESPESA_NAO_PAGA')).toBe(true);
    expect((await call('get', cor, '/alertas')).body).toHaveLength(0);
    await A('patch', `/despesas/lancamentos/${l}/status`, { status: 'PAGO', data_pagamento: '2025-01-05' });
    expect((await call('get', fin, '/alertas')).body.some((a: { tipo: string }) => a.tipo === 'DESPESA_NAO_PAGA')).toBe(false); // resolvido na hora do pagamento
    expect((await svc.verificarDespesas()).criados).toBe(0); // e o job não recria
  });
});

describe('CRM: funil configurável', () => {
  let lead: string, estagios: { id: string; nome: string; tipo: string }[];
  it('pipeline padrão tem 7 estágios', async () => {
    const p = (await A('get', '/crm/pipeline?funil=VENDA')).body;
    expect(p.estagios).toHaveLength(7);
    estagios = p.estagios;
  });
  it('cria lead com contato inline no 1º estágio, move e registra histórico', async () => {
    const r = await A('post', '/leads', { finalidade: 'COMPRA', origem: 'Indicação', pessoa: { nome_razao: 'Cliente Novo', telefone: '11999990000' }, orcamento_max: '800000' });
    expect(r.status).toBe(201);
    lead = r.body.id;
    expect(r.body.estagio.nome).toBe('Novo Lead');
    const mv = await A('patch', `/leads/${lead}/estagio`, { estagio_id: estagios.find((e) => e.nome === 'Proposta')!.id });
    expect(mv.body.estagio.nome).toBe('Proposta');
    const h = (await A('get', `/leads/${lead}/historico`)).body;
    expect(h).toHaveLength(2);
    expect((await A('post', `/leads/${lead}/interacoes`, { canal: 'WHATSAPP', resumo: 'Enviou proposta' })).status).toBe(201);
    expect((await A('get', `/leads/${lead}/interacoes`)).body).toHaveLength(1);
  });
  it('Perdido exige motivo; estágio com leads não pode ser excluído', async () => {
    const perdido = estagios.find((e) => e.tipo === 'PERDIDO')!.id;
    const sem = await A('patch', `/leads/${lead}/estagio`, { estagio_id: perdido });
    expect(sem.status).toBe(422);
    expect(sem.body.errors.motivo_perda).toBeTruthy();
    expect((await A('patch', `/leads/${lead}/estagio`, { estagio_id: perdido, motivo_perda: 'Comprou outro imóvel' })).body.motivo_perda).toBe('Comprou outro imóvel');
    expect((await A('delete', `/crm/estagios/${perdido}`)).status).toBe(409);
  });
  it('estágios são configuráveis (criar, renomear, reordenar)', async () => {
    const n = await A('post', '/crm/estagios', { funil: 'VENDA', nome: 'Due diligence', ordem: 3, tipo: 'ABERTO', cor: '#112233' });
    expect(n.status).toBe(201);
    expect((await A('patch', `/crm/estagios/${n.body.id}`, { nome: 'Análise jurídica' })).body.nome).toBe('Análise jurídica');
    expect((await A('get', '/crm/pipeline?funil=VENDA')).body.estagios).toHaveLength(8);
    expect((await A('delete', `/crm/estagios/${n.body.id}`)).status).toBe(204);
  });
  it('perfil Financeiro só consulta o CRM (sem criar leads)', async () => {
    expect((await call('get', fin, '/crm/pipeline')).status).toBe(200);
    expect((await call('post', fin, '/leads', { pessoa: { nome_razao: 'Teste X' } })).status).toBe(403);
  });
});

describe('Relatórios (Ficha do Imóvel e Ficha Financeira)', () => {
  let imv: string;
  it('gera PDF e Excel com assinatura válida', async () => {
    imv = (await A('post', '/imoveis', { tipo: 'casa', titulo: 'Casa do relatório', logradouro: 'Rua A', cidade: 'Campinas', uf: 'SP', preco_venda: '500000' })).body.id;
    await A('put', `/imoveis/${imv}/aquisicao`, { data_aquisicao: '2025-01-10', valor_pago: '200000', impostos: '6000' });
    const pdf = await c.binario(c.http.get(`/api/v1/imoveis/${imv}/ficha.pdf`).set(H(adm)));
    expect(pdf.status).toBe(200);
    expect(pdf.headers['content-type']).toMatch(/pdf/);
    expect((pdf.body as Buffer).subarray(0, 4).toString()).toBe('%PDF');
    const x = await c.binario(c.http.get(`/api/v1/imoveis/${imv}/ficha.xlsx`).set(H(adm)));
    expect((x.body as Buffer).subarray(0, 2).toString()).toBe('PK');
    const fpdf = await c.binario(c.http.get(`/api/v1/imoveis/${imv}/ficha-financeira.pdf?indice=IPCA`).set(H(adm)));
    expect((fpdf.body as Buffer).subarray(0, 4).toString()).toBe('%PDF');
    const fx = await c.binario(c.http.get(`/api/v1/imoveis/${imv}/ficha-financeira.xlsx`).set(H(adm)));
    expect((fx.body as Buffer).subarray(0, 2).toString()).toBe('PK');
  });
  it('corretor exporta a ficha só do que é dele e nunca a financeira', async () => {
    expect((await call('get', cor, `/imoveis/${imv}/ficha-financeira.pdf`)).status).toBe(403);
    expect((await call('get', cor, `/imoveis/${imv}/ficha.pdf`)).status).toBe(403); // imóvel de outro responsável
  });
});

describe('Google Calendar: mapeamentos puros', () => {
  const comp = { id: 'c1', titulo: 'Visita', descricao: 'Levar chaves', local: 'Rua A', inicio_em: new Date('2025-03-01T13:00:00Z'), fim_em: new Date('2025-03-01T14:00:00Z'), status: 'CONCLUIDO' } as never;
  it('compromisso → evento inclui código do imóvel, participantes e id de origem', () => {
    const ev = paraEventoGoogle(comp, 'IMV-000007', ['a@x.com']);
    expect(ev.description).toContain('#IMV-000007');
    expect(ev.attendees).toEqual([{ email: 'a@x.com' }]);
    expect(ev.extendedProperties.private.inovabens_compromisso_id).toBe('c1');
    expect(ev.colorId).toBe('2');
    expect(ev.start.timeZone).toBe('America/Sao_Paulo');
  });
  it('evento → compromisso; dia inteiro respeita end.date exclusivo', () => {
    const d = deEventoGoogle({ id: 'e', summary: 'Vistoria', start: { date: '2025-03-10' }, end: { date: '2025-03-11' } })!;
    expect(d.inicio_em.toISOString()).toBe('2025-03-10T03:00:00.000Z');
    expect(d.fim_em.toISOString()).toBe('2025-03-11T02:59:59.000Z'); // ainda dia 10 em Brasília
    expect(deEventoGoogle({ id: 'e' })).toBeNull();
    expect(deEventoGoogle({ id: 'e', start: { dateTime: '2025-03-10T10:00:00Z' }, end: { dateTime: '2025-03-10T09:00:00Z' } })!.fim_em.toISOString()).toBe('2025-03-10T10:00:00.000Z');
  });
  it('só importa eventos que citam o código do imóvel', () => {
    expect(codigoImovelNoTexto({ id: 'e', summary: 'Visita #imv-000012' })).toBe('IMV-000012');
    expect(codigoImovelNoTexto({ id: 'e', description: 'x #IMV-000003 y' })).toBe('IMV-000003');
    expect(codigoImovelNoTexto({ id: 'e', summary: 'Dentista' })).toBeNull();
  });
});
