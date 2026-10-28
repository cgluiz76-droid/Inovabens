import { Inject, Injectable } from '@nestjs/common';
import { and, asc, eq, isNull, lte } from 'drizzle-orm';
import type { CODIGO_INDICE } from '@inovabens/shared';
import type { AuthUser } from '../common/auth.types';
import { diasEntre, hojeBR } from '../common/datas';
import { assertAcessoImovel } from '../common/escopo';
import { fromCents, toCents } from '../common/money';
import { DB, Database } from '../db/db.module';
import { aquisicao_itens, aquisicoes, despesa_categorias, despesa_lancamentos, despesas, receitas } from '../db/schema';
import { IndicesService } from '../indices/indices.module';

type Codigo = (typeof CODIGO_INDICE)[number];
interface Mov { data: string; tipo: 'SAIDA' | 'ENTRADA'; descricao: string; cents: bigint; aq?: boolean }

const ratio = (a: bigint, b: bigint) => (b === 0n ? null : Math.round((Number(a) / Number(b)) * 1e6) / 1e6);
const fmtMes = (m: string | null) => (m ? `${m.slice(5, 7)}/${m.slice(0, 4)}` : '—');

/** Taxa interna de retorno anualizada (XIRR) por bissecção; null se não há saída e entrada. */
export function xirr(fluxo: { data: string; v: number }[]): number | null {
  if (!fluxo.some((f) => f.v < 0) || !fluxo.some((f) => f.v > 0)) return null;
  const t0 = Math.min(...fluxo.map((f) => Date.parse(f.data)));
  const npv = (r: number) => fluxo.reduce((s, f) => s + f.v / Math.pow(1 + r, (Date.parse(f.data) - t0) / (365 * 864e5)), 0);
  let lo = -0.9999, hi = 100;
  if (npv(lo) * npv(hi) > 0) return null;
  for (let i = 0; i < 200; i++) {
    const mid = (lo + hi) / 2;
    if (npv(lo) * npv(mid) <= 0) hi = mid; else lo = mid;
  }
  return Math.round(((lo + hi) / 2) * 1e6) / 1e6;
}

@Injectable()
export class RetornoService {
  constructor(@Inject(DB) private readonly db: Database, private readonly indices: IndicesService) {}

  /**
   * Retorno do investimento no imóvel, a partir do fluxo de caixa datado:
   *  - SAÍDAS: valor pago + itens de aquisição + despesas PAGAS (OPEX);
   *  - ENTRADAS: receitas líquidas (venda/locação), já descontados custos de venda e tributos;
   *  - passivo pendente: despesas NAO_PAGO (fora do custo, destacadas);
   *  - correção: cada saída é multiplicada por F(data final)/F(data da saída) do índice escolhido;
   *  - percentuais em fração (0.153 = 15,3%).
   */
  async calcular(user: AuthUser, imovelId: string, indice: Codigo = 'IPCA', ateParam?: string) {
    await assertAcessoImovel(this.db, user, imovelId);
    const ate = ateParam ?? hojeBR();
    const avisos: string[] = [];
    const movs: Mov[] = [];

    const [aq] = await this.db.select().from(aquisicoes).where(eq(aquisicoes.imovel_id, imovelId));
    if (!aq) avisos.push('Aquisição não cadastrada: o custo de aquisição está zerado.');
    else if (aq.data_aquisicao <= ate) {
      movs.push({ data: aq.data_aquisicao, tipo: 'SAIDA', descricao: 'Valor pago na aquisição', cents: toCents(aq.valor_pago), aq: true });
      const itens = await this.db.select().from(aquisicao_itens).where(eq(aquisicao_itens.aquisicao_id, aq.id)).orderBy(asc(aquisicao_itens.criado_em));
      let detalhado = 0n;
      for (const it of itens) {
        const c = toCents(it.valor); detalhado += c;
        movs.push({ data: it.data_pagamento ?? aq.data_aquisicao, tipo: 'SAIDA', descricao: `${it.tipo === 'IMPOSTO' ? 'Imposto' : it.tipo === 'TAXA' ? 'Taxa' : 'Despesa'} de aquisição: ${it.descricao}`, cents: c, aq: true });
      }
      const residual = toCents(aq.impostos) + toCents(aq.taxas) + toCents(aq.despesas_diversas) - detalhado;
      if (residual > 0n) movs.push({ data: aq.data_aquisicao, tipo: 'SAIDA', descricao: 'Impostos/taxas/despesas de aquisição (sem detalhamento)', cents: residual, aq: true });
    }

    const lancs = await this.db.select({ l: despesa_lancamentos, d: despesas.descricao, cat: despesa_categorias.nome }).from(despesa_lancamentos)
      .innerJoin(despesas, eq(despesas.id, despesa_lancamentos.despesa_id)).innerJoin(despesa_categorias, eq(despesa_categorias.id, despesas.categoria_id))
      .where(and(eq(despesas.imovel_id, imovelId), isNull(despesas.excluido_em), lte(despesa_lancamentos.competencia, ate)));
    let pendente = 0n;
    for (const { l, d, cat } of lancs) {
      if (l.status === 'NAO_PAGO') pendente += toCents(l.valor);
      if (l.status === 'PAGO') {
        const data = l.data_pagamento ?? l.vencimento ?? l.competencia;
        if (data <= ate) movs.push({ data, tipo: 'SAIDA', descricao: `${cat}: ${d} (${fmtMes(l.competencia)})`, cents: toCents(l.valor) });
      }
    }

    const recs = await this.db.select().from(receitas).where(and(eq(receitas.imovel_id, imovelId), isNull(receitas.excluido_em), lte(receitas.data, ate)));
    for (const r of recs) movs.push({ data: r.data, tipo: 'ENTRADA', descricao: `${r.tipo === 'VENDA' ? 'Venda' : r.tipo === 'LOCACAO' ? 'Locação' : 'Outra receita'} (líquido)`, cents: toCents(r.valor_liquido) });

    movs.sort((a, b) => a.data.localeCompare(b.data) || (a.tipo === 'SAIDA' ? -1 : 1));
    const saidas = movs.filter((m) => m.tipo === 'SAIDA');
    const entradas = movs.filter((m) => m.tipo === 'ENTRADA');
    const custo = saidas.reduce((s, m) => s + m.cents, 0n);
    const receita = entradas.reduce((s, m) => s + m.cents, 0n);
    const custoAquisicao = saidas.filter((m) => m.aq).reduce((s, m) => s + m.cents, 0n);

    // Data final da correção: a venda (se houver), senão a data de corte.
    const venda = recs.filter((r) => r.tipo === 'VENDA').map((r) => r.data).sort().at(-1);
    const dataFinal = venda ?? ate;

    // Correção monetária pelo índice escolhido
    const f = await this.indices.fatores(indice);
    let corrigido: bigint | null = null;
    const fluxo = movs.map((m) => ({ data: m.data, tipo: m.tipo, descricao: m.descricao, valor: fromCents(m.cents), valor_corrigido: null as string | null }));
    if (!f.primeiroMes) {
      avisos.push(`Série do índice ${indice} vazia: sincronize os índices (Administração → Configurações → Índices econômicos) para calcular a correção.`);
    } else {
      corrigido = 0n;
      const fFim = f.fator(dataFinal);
      fluxo.forEach((x, i) => {
        if (x.tipo !== 'SAIDA') return;
        const c = BigInt(Math.round(Number(movs[i].cents) * (fFim / f.fator(x.data))));
        x.valor_corrigido = fromCents(c); corrigido! += c;
      });
      if (f.primeiroMes > (saidas[0]?.data ?? '').slice(0, 7)) avisos.push(`A série ${indice} começa em ${fmtMes(f.primeiroMes)}, depois da primeira saída: a correção anterior a isso não foi aplicada.`);
      if (f.ultimoMes && f.ultimoMes < dataFinal.slice(0, 7)) avisos.push(`A série ${indice} vai até ${fmtMes(f.ultimoMes)}; meses seguintes sem correção.`);
    }

    const temReceita = receita > 0n;
    if (!temReceita) avisos.push('Sem receitas registradas: ROI, TIR e MOIC só são calculados após a venda/locação.');
    const resultado = receita - custo;
    const tir = temReceita ? xirr(movs.map((m) => ({ data: m.data, v: Number(m.tipo === 'SAIDA' ? -m.cents : m.cents) / 100 }))) : null;
    const inicio = saidas[0]?.data;

    return {
      imovel_id: imovelId, ate, data_final: dataFinal, indice,
      custo_aquisicao: fromCents(custoAquisicao),
      custo_total: fromCents(custo),
      passivo_pendente: fromCents(pendente),
      custo_corrigido: corrigido === null ? null : fromCents(corrigido),
      receita_liquida: fromCents(receita),
      resultado: fromCents(resultado),
      roi: temReceita ? ratio(resultado, custo) : null,
      roi_real: temReceita && corrigido !== null ? ratio(receita - corrigido, corrigido) : null,
      tir_anual: tir,
      moic: temReceita ? ratio(receita, custo) : null,
      prazo_dias: inicio ? diasEntre(inicio, dataFinal) : null,
      avisos,
      fluxo,
    };
  }
}
