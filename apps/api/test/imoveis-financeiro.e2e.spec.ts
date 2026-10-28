import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { ADMIN, Ctx, criarApp } from './helpers';

let c: Ctx;
let adm: string;
const A = () => ({ Authorization: `Bearer ${adm}` });
const post = (url: string, body: object) => c.http.post(`/api/v1${url}`).set(A()).send(body);
const put = (url: string, body: object) => c.http.put(`/api/v1${url}`).set(A()).send(body);
const patch = (url: string, body: object) => c.http.patch(`/api/v1${url}`).set(A()).send(body);
const get = (url: string) => c.http.get(`/api/v1${url}`).set(A());

beforeAll(async () => { c = await criarApp(); adm = (await c.login(ADMIN.email, ADMIN.senha)).token; });
afterAll(async () => { await c.app.close(); await c.client.close(); });

describe('Pessoas e imóveis', () => {
  let imovel: string, p1: string, p2: string;

  it('valida CPF/CNPJ e impede duplicidade', async () => {
    expect((await post('/pessoas', { nome_razao: 'Fulano', cpf_cnpj: '123' })).status).toBe(422);
    const a = await post('/pessoas', { nome_razao: 'Maria Proprietária', cpf_cnpj: '529.982.247-25', email: 'MARIA@X.com', papeis: [{ papel: 'PROPRIETARIO' }] });
    expect(a.status).toBe(201);
    expect(a.body.cpf_cnpj).toBe('52998224725');
    expect(a.body.email).toBe('maria@x.com');
    p1 = a.body.id;
    expect((await post('/pessoas', { nome_razao: 'Outra', cpf_cnpj: '52998224725' })).status).toBe(409);
    p2 = (await post('/pessoas', { nome_razao: 'João Sócio', papeis: [{ papel: 'PROPRIETARIO' }, { papel: 'PRESTADOR', especialidade: 'Advogado' }] })).body.id;
    const lista = await get('/pessoas?papel=PRESTADOR');
    expect(lista.body.total).toBe(1);
    expect(lista.body.data[0].papeis).toHaveLength(2);
  });

  it('cria a ficha do imóvel com código sequencial e campos de mercado', async () => {
    const r = await post('/imoveis', {
      tipo: 'apartamento', titulo: 'Apto com pendências documentais', finalidade: 'VENDA', situacao: 'EM_REGULARIZACAO',
      cep: '01310-100', logradouro: 'Av. Paulista', numero: '1000', bairro: 'Bela Vista', cidade: 'São Paulo', uf: 'sp',
      area_total_m2: 85.5, quartos: 3, suites: 1, banheiros: 2, vagas: 1, preco_venda: '950000', matricula: '12345', cartorio: '5º RI',
      descricao_interna: 'IPTU em atraso', atributos: { andar: 7 }, ficha_inexistente: 'ignorado',
    });
    expect(r.status).toBe(201);
    imovel = r.body.id;
    expect(r.body.codigo).toBe('IMV-000001');
    expect(r.body.uf).toBe('SP');
    expect(r.body.preco_venda).toBe('950000.00');
    expect(r.body.area_total_m2).toBe('85.50');
    expect(r.body.ficha_inexistente).toBeUndefined();
    expect((await post('/imoveis', { tipo: 'casa', titulo: 'Casa 2' })).body.codigo).toBe('IMV-000002');
  });

  it('proprietários: a soma dos percentuais vigentes deve ser 100%', async () => {
    const ruim = await put(`/imoveis/${imovel}/proprietarios`, [{ pessoa_id: p1, percentual: 60 }, { pessoa_id: p2, percentual: 30 }]);
    expect(ruim.status).toBe(422);
    expect(ruim.body.errors.proprietarios[0]).toMatch(/100%/);
    const ok = await put(`/imoveis/${imovel}/proprietarios`, [{ pessoa_id: p1, percentual: 60, principal: true }, { pessoa_id: p2, percentual: '40.00' }]);
    expect(ok.status).toBe(200);
    expect(ok.body).toHaveLength(2);
  });

  it('características, envolvidos, regularização e busca', async () => {
    expect((await put(`/imoveis/${imovel}/caracteristicas`, [{ chave: 'Piscina', valor: null, publico: true }, { chave: 'Chave com porteiro', publico: false }])).body).toHaveLength(2);
    expect((await put(`/imoveis/${imovel}/envolvidos`, [{ pessoa_id: p2, papel: 'PRESTADOR', observacao: 'Regulariza matrícula' }])).status).toBe(200);
    const reg = await post(`/imoveis/${imovel}/regularizacoes`, { tipo: 'RETIFICACAO_AREA', profissional_id: p2, custo_estimado: '3500', prazo_previsto: '2026-12-01' });
    expect(reg.status).toBe(201);
    expect((await patch(`/regularizacoes/${reg.body.id}`, { status: 'EM_ANDAMENTO' })).body.status).toBe('EM_ANDAMENTO');
    const lista = await get(`/imoveis/${imovel}/regularizacoes`);
    expect(lista.body[0].profissional.nome_razao).toBe('João Sócio');
    expect((await get('/imoveis?q=paulista')).body.total).toBe(1);
    expect((await get('/imoveis?situacao=CAPTACAO')).body.total).toBe(1);
    const det = await get(`/imoveis/${imovel}`);
    expect(det.body.caracteristicas).toHaveLength(2);
    expect(det.body.proprietarios[0].pessoa.nome_razao).toBeTruthy();
  });

  it('upload sem S3 configurado devolve 503 claro; exclusão é lógica', async () => {
    const up = await c.http.post(`/api/v1/imoveis/${imovel}/midias`).set(A()).attach('arquivo', Buffer.from('x'), 'foto.jpg');
    expect(up.status).toBe(503);
    const tmp = (await post('/imoveis', { tipo: 'terreno', titulo: 'Terreno temporário' })).body.id;
    expect((await c.http.delete(`/api/v1/imoveis/${tmp}`).set(A())).status).toBe(204);
    expect((await get(`/imoveis/${tmp}`)).status).toBe(404);
  });

  it('memorandos: cria, fixa e lista com autor', async () => {
    const m = await post(`/imoveis/${imovel}/memorandos`, { tipo: 'ACAO_ESTRATEGICA', titulo: 'Contratar topógrafo', corpo: 'Orçamento de 3 profissionais', fixado: true });
    expect(m.status).toBe(201);
    await post(`/imoveis/${imovel}/memorandos`, { titulo: 'Ligar para o cartório' });
    const lista = await get(`/imoveis/${imovel}/memorandos`);
    expect(lista.body).toHaveLength(2);
    expect(lista.body[0].fixado).toBe(true);
    expect(lista.body[0].autor.nome).toBe('Administrador');
  });
});

describe('Financeiro: aquisição (CAPEX)', () => {
  let imv: string;
  it('Total de Aquisição = valor pago + impostos + taxas + despesas (calculado no banco)', async () => {
    imv = (await post('/imoveis', { tipo: 'casa', titulo: 'Casa leilão' })).body.id;
    expect((await get(`/imoveis/${imv}/aquisicao`)).status).toBe(404);
    const r = await put(`/imoveis/${imv}/aquisicao`, { data_aquisicao: '2025-01-10', valor_pago: '300000.00', impostos: '9000', taxas: 2500.5, despesas_diversas: '1000.25', total_aquisicao: '1.00' });
    expect(r.status).toBe(200);
    expect(r.body.total_aquisicao).toBe('312500.75'); // o total enviado pelo cliente é ignorado
  });

  it('itens detalhados recalculam os componentes e o total; PUT não os sobrescreve', async () => {
    await post(`/imoveis/${imv}/aquisicao/itens`, { tipo: 'IMPOSTO', descricao: 'ITBI', valor: '6000', data_pagamento: '2025-01-12' });
    await post(`/imoveis/${imv}/aquisicao/itens`, { tipo: 'IMPOSTO', descricao: 'Foro', valor: '500' });
    await post(`/imoveis/${imv}/aquisicao/itens`, { tipo: 'TAXA', descricao: 'Registro', valor: '1200' });
    const ult = await post(`/imoveis/${imv}/aquisicao/itens`, { tipo: 'DESPESA', descricao: 'Certidões', valor: '300' });
    expect(ult.body.impostos).toBe('6500.00');
    expect(ult.body.total_aquisicao).toBe('308000.00');
    const reput = await put(`/imoveis/${imv}/aquisicao`, { data_aquisicao: '2025-01-10', valor_pago: '300000', impostos: '99999' });
    expect(reput.body.impostos).toBe('6500.00');
    expect(reput.body.total_aquisicao).toBe('308000.00');
    const apagado = await c.http.delete(`/api/v1/aquisicao/itens/${ult.body.itens[0].id}`).set(A());
    expect(apagado.status).toBe(200);
    expect(apagado.body.total_aquisicao).toBeDefined();
  });

  it('item sem aquisição cadastrada → 409', async () => {
    const outro = (await post('/imoveis', { tipo: 'casa', titulo: 'Sem aquisição' })).body.id;
    expect((await post(`/imoveis/${outro}/aquisicao/itens`, { tipo: 'TAXA', descricao: 'Taxa qualquer', valor: '1' })).status).toBe(409);
  });

  it('toda escrita financeira gera auditoria', async () => {
    const r = await get(`/auditoria?entidade=aquisicoes&page=1`);
    expect(r.status).toBe(200);
    expect(r.body.total).toBeGreaterThanOrEqual(2);
    expect(r.body.data[0].usuario.nome).toBe('Administrador');
  });
});

describe('Financeiro: despesas (OPEX) e checklist mensal', () => {
  let imv: string, cat: string;
  let lancNaoPago: string;

  it('gera a competência do mês de forma idempotente e respeita a recorrência', async () => {
    imv = (await post('/imoveis', { tipo: 'casa', titulo: 'Casa com despesas' })).body.id;
    cat = (await get('/categorias-despesa')).body.find((x: { nome: string }) => x.nome === 'IPTU').id;
    await post(`/imoveis/${imv}/despesas`, { categoria_id: cat, descricao: 'IPTU mensal', recorrencia: 'MENSAL', dia_vencimento: 31, valor_previsto: '250.00', inicio_em: '2025-01-01' });
    await post(`/imoveis/${imv}/despesas`, { categoria_id: cat, descricao: 'Seguro anual', recorrencia: 'ANUAL', valor_previsto: '1200', inicio_em: '2025-03-10' });
    await post(`/imoveis/${imv}/despesas`, { categoria_id: cat, descricao: 'Reforma única', recorrencia: 'UNICA', valor_previsto: '8000', inicio_em: '2025-02-05' });
    const fev = await get(`/imoveis/${imv}/despesas/checklist?mes=2025-02`);
    expect(fev.body.itens.map((i: { descricao: string }) => i.descricao).sort()).toEqual(['IPTU mensal', 'Reforma única']);
    expect(fev.body.itens.find((i: { descricao: string }) => i.descricao === 'IPTU mensal').vencimento).toBe('2025-02-28'); // dia 31 → último dia
    expect(fev.body.resumo).toEqual({ pago: 0, nao_pago: 0, nao_se_aplica: 0, a_marcar: 2 });
    expect((await get(`/imoveis/${imv}/despesas/checklist?mes=2025-02`)).body.itens).toHaveLength(2); // não duplica
    const mar = (await get(`/imoveis/${imv}/despesas/checklist?mes=2026-03`)).body.itens.map((i: { descricao: string }) => i.descricao).sort();
    expect(mar).toEqual(['IPTU mensal', 'Seguro anual']); // anual: só em março
    expect((await get(`/imoveis/${imv}/despesas/checklist?mes=2025-13`)).status).toBe(422);
  });

  it('REGRA: status Não Pago exige justificativa; Pago e Não se Aplica não', async () => {
    const itens = (await get(`/imoveis/${imv}/despesas/checklist?mes=2025-02`)).body.itens as { lancamento_id: string; descricao: string }[];
    const iptu = itens.find((i) => i.descricao === 'IPTU mensal')!, reforma = itens.find((i) => i.descricao === 'Reforma única')!;
    lancNaoPago = iptu.lancamento_id;

    const sem = await patch(`/despesas/lancamentos/${iptu.lancamento_id}/status`, { status: 'NAO_PAGO' });
    expect(sem.status).toBe(422);
    expect(sem.body.errors.justificativa[0]).toMatch(/obrigatória/i);
    expect((await patch(`/despesas/lancamentos/${iptu.lancamento_id}/status`, { status: 'NAO_PAGO', justificativa: '   curto   ' })).status).toBe(422);
    const ok = await patch(`/despesas/lancamentos/${iptu.lancamento_id}/status`, { status: 'NAO_PAGO', justificativa: 'Boleto não chegou; solicitada 2ª via à prefeitura' });
    expect(ok.status).toBe(200);
    expect(ok.body.status).toBe('NAO_PAGO');

    const pago = await patch(`/despesas/lancamentos/${reforma.lancamento_id}/status`, { status: 'PAGO', data_pagamento: '2025-02-20', valor: '8200.00' });
    expect(pago.body.data_pagamento).toBe('2025-02-20');
    expect(pago.body.valor).toBe('8200.00');
    expect(pago.body.justificativa).toBeNull();

    const res = (await get(`/imoveis/${imv}/despesas/checklist?mes=2025-02`)).body.resumo;
    expect(res).toEqual({ pago: 1, nao_pago: 1, nao_se_aplica: 0, a_marcar: 0 });
    // corrigir NAO_PAGO → PAGO limpa a justificativa
    const virou = await patch(`/despesas/lancamentos/${iptu.lancamento_id}/status`, { status: 'PAGO' });
    expect(virou.body.justificativa).toBeNull();
    expect(virou.body.data_pagamento).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });

  it('a constraint do banco é a última barreira contra NAO_PAGO sem justificativa', async () => {
    await expect(c.client.query("update despesa_lancamentos set status = 'NAO_PAGO', justificativa = null where id = $1::uuid", [lancNaoPago])).rejects.toThrow();
  });

  it('histórico do imóvel e exclusão lógica de despesa', async () => {
    const hist = await get(`/imoveis/${imv}/despesas/lancamentos`);
    expect(hist.body.length).toBeGreaterThanOrEqual(4);
    const lista = (await get(`/imoveis/${imv}/despesas`)).body as { id: string; categoria: string }[];
    expect(lista[0].categoria).toBe('IPTU');
    expect((await c.http.delete(`/api/v1/despesas/${lista[0].id}`).set(A())).status).toBe(204);
    expect((await get(`/imoveis/${imv}/despesas`)).body).toHaveLength(2);
  });
});

describe('Retorno do investimento (ROI, TIR, MOIC, correção por índice)', () => {
  let imv: string;

  it('sem receitas: custos calculados, indicadores de retorno indisponíveis, com aviso', async () => {
    imv = (await post('/imoveis', { tipo: 'casa', titulo: 'Casa do retorno' })).body.id;
    await put(`/imoveis/${imv}/aquisicao`, { data_aquisicao: '2025-01-10', valor_pago: '100000.00' });
    const r = (await get(`/imoveis/${imv}/retorno`)).body;
    expect(r.custo_total).toBe('100000.00');
    expect(r.roi).toBeNull();
    expect(r.avisos.join(' ')).toMatch(/Sem receitas/);
    expect(r.avisos.join(' ')).toMatch(/Série do índice IPCA vazia/);
  });

  it('cenário completo conferido à mão', async () => {
    const cat = (await get('/categorias-despesa')).body[0].id;
    await post(`/imoveis/${imv}/despesas`, { categoria_id: cat, descricao: 'Reforma', recorrencia: 'UNICA', inicio_em: '2025-04-01', dia_vencimento: 15, valor_previsto: '10000' });
    await post(`/imoveis/${imv}/despesas`, { categoria_id: cat, descricao: 'IPTU em aberto', recorrencia: 'UNICA', inicio_em: '2025-05-01', dia_vencimento: 10, valor_previsto: '500' });
    const abr = (await get(`/imoveis/${imv}/despesas/checklist?mes=2025-04`)).body.itens[0];
    await patch(`/despesas/lancamentos/${abr.lancamento_id}/status`, { status: 'PAGO', data_pagamento: '2025-04-15' });
    const mai = (await get(`/imoveis/${imv}/despesas/checklist?mes=2025-05`)).body.itens[0];
    await patch(`/despesas/lancamentos/${mai.lancamento_id}/status`, { status: 'NAO_PAGO', justificativa: 'Aguardando regularização do cadastro' });
    const venda = await post(`/imoveis/${imv}/receitas`, { tipo: 'VENDA', data: '2025-07-20', valor_bruto: '150000', custos_venda: '9000', tributos: '1000' });
    expect(venda.body.valor_liquido).toBe('140000.00');
    for (const m of ['01', '02', '03', '04', '05', '06', '07']) expect((await put('/indices/IPCA/serie', { competencia: `2025-${m}`, variacao_mensal_pct: 1 })).status).toBe(200);

    const r = (await get(`/imoveis/${imv}/retorno?indice=IPCA&ate=2025-12-31`)).body;
    expect(r.custo_aquisicao).toBe('100000.00');
    expect(r.custo_total).toBe('110000.00'); // 100.000 + 10.000 (a despesa Não Paga fica fora)
    expect(r.passivo_pendente).toBe('500.00');
    expect(r.receita_liquida).toBe('140000.00');
    expect(r.resultado).toBe('30000.00');
    expect(r.roi).toBeCloseTo(30000 / 110000, 6);
    expect(r.moic).toBeCloseTo(140000 / 110000, 6);
    expect(r.data_final).toBe('2025-07-20');
    expect(r.prazo_dias).toBe(191);
    // correção: 100.000 × 1,01^6 (jan→jul) + 10.000 × 1,01^3 (abr→jul)
    expect(r.custo_corrigido).toBe('116455.03');
    expect(r.roi_real).toBeCloseTo((140000 - 116455.03) / 116455.03, 5);
    // TIR: o VPL dos fluxos descontados a essa taxa deve ser ~0
    const t0 = Date.parse('2025-01-10');
    const npv = [[-100000, '2025-01-10'], [-10000, '2025-04-15'], [140000, '2025-07-20']].reduce((s, [v, d]) => s + (v as number) / Math.pow(1 + r.tir_anual, (Date.parse(d as string) - t0) / (365 * 864e5)), 0);
    expect(Math.abs(npv)).toBeLessThan(0.5);
    expect(r.tir_anual).toBeGreaterThan(0.5);
    expect(r.fluxo).toHaveLength(3);
    expect(r.fluxo[0].valor_corrigido).toBe('106152.02');
    expect(r.avisos).toEqual([]);
  });

  it('índice vazio ou data de corte antes da venda mudam o resultado', async () => {
    const antes = (await get(`/imoveis/${imv}/retorno?ate=2025-06-30`)).body;
    expect(antes.receita_liquida).toBe('0.00');
    expect(antes.roi).toBeNull();
    expect((await get(`/imoveis/${imv}/retorno?indice=SELIC`)).body.custo_corrigido).toBeNull();
    expect((await get(`/imoveis/${imv}/retorno?indice=XYZ`)).status).toBe(422);
  });
});
