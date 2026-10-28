'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Download, Trash2 } from 'lucide-react';
import { CODIGO_INDICE, RECORRENCIA, TIPO_ITEM_AQUISICAO, TIPO_RECEITA } from '@inovabens/shared';
import { api, ApiError, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { brl, data, mesAtual, pct, rotulo } from '@/lib/fmt';
import { Badge, Campo, Carregando, Erro, Modal, Select, Vazio } from '../ui';

const num = (s: string) => s.replace(',', '.');

export default function Financeiro({ imovelId }: { imovelId: string }) {
  const { pode } = useAuth(); const editar = pode('financeiro:editar');
  const [indice, setIndice] = useState('IPCA'); const [exportErro, setExportErro] = useState<string | null>(null);
  const baixar = async (ext: string) => { setExportErro(null); try { await api.baixar(`/imoveis/${imovelId}/ficha-financeira.${ext}?indice=${indice}`, `ficha-financeira.${ext}`); } catch (e) { setExportErro(msgErro(e)); } };
  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-slate-500">Custo total = valor pago + impostos + taxas + despesas (calculado automaticamente).</p>
        {pode('financeiro:exportar') && <div className="flex gap-2"><button className="btn-ghost" onClick={() => baixar('pdf')}><Download size={14} />Ficha Financeira PDF</button><button className="btn-ghost" onClick={() => baixar('xlsx')}><Download size={14} />Excel</button></div>}
      </div>
      <Erro msg={exportErro} />
      <Aquisicao imovelId={imovelId} editar={editar} />
      <Despesas imovelId={imovelId} editar={editar} />
      <Receitas imovelId={imovelId} editar={editar} />
      <Retorno imovelId={imovelId} indice={indice} setIndice={setIndice} />
    </div>
  );
}

/* ------------------------------ Aquisição ------------------------------ */
interface Aq { data_aquisicao: string; valor_pago: string; impostos: string; taxas: string; despesas_diversas: string; total_aquisicao: string; forma_pagamento?: string | null; itens: { id: string; tipo: string; descricao: string; valor: string; data_pagamento: string | null }[] }
function Aquisicao({ imovelId, editar }: { imovelId: string; editar: boolean }) {
  const qc = useQueryClient(); const chave = ['aquisicao', imovelId];
  const q = useQuery({ queryKey: chave, queryFn: async () => { try { return await api.get<Aq>(`/imoveis/${imovelId}/aquisicao`); } catch (e) { if (e instanceof ApiError && e.status === 404) return null; throw e; } } });
  const [f, setF] = useState<{ data_aquisicao: string; valor_pago: string; impostos: string; taxas: string; despesas_diversas: string; forma_pagamento: string } | null>(null);
  const [item, setItem] = useState({ tipo: 'IMPOSTO', descricao: '', valor: '', data_pagamento: '' });
  const a = q.data; const form = f ?? { data_aquisicao: a?.data_aquisicao ?? '', valor_pago: a?.valor_pago ?? '', impostos: a?.impostos ?? '', taxas: a?.taxas ?? '', despesas_diversas: a?.despesas_diversas ?? '', forma_pagamento: a?.forma_pagamento ?? '' };
  const inval = () => { setF(null); qc.invalidateQueries({ queryKey: chave }); qc.invalidateQueries({ queryKey: ['retorno', imovelId] }); };
  const salvar = useMutationSimple(() => api.put(`/imoveis/${imovelId}/aquisicao`, { data_aquisicao: form.data_aquisicao, valor_pago: num(form.valor_pago || '0'), ...(a?.itens.length ? {} : { impostos: num(form.impostos || '0'), taxas: num(form.taxas || '0'), despesas_diversas: num(form.despesas_diversas || '0') }), forma_pagamento: form.forma_pagamento || null }), inval);
  const addItem = useMutationSimple(() => api.post(`/imoveis/${imovelId}/aquisicao/itens`, { ...item, valor: num(item.valor), data_pagamento: item.data_pagamento || null }), () => { setItem({ tipo: 'IMPOSTO', descricao: '', valor: '', data_pagamento: '' }); inval(); });
  const delItem = useMutationSimple((id: string) => api.del(`/aquisicao/itens/${id}`), inval);
  const upd = (k: string, v: string) => setF({ ...form, [k]: v });
  const comItens = !!a?.itens.length;
  if (q.isLoading) return <Carregando />;
  return (
    <section className="card"><h2 className="mb-3 font-semibold">Aquisição (CAPEX)</h2>
      <Erro msg={salvar.erro ?? addItem.erro ?? delItem.erro} />
      <div className="grid gap-3 md:grid-cols-4">
        <Campo label="Data da aquisição"><input type="date" className="input" disabled={!editar} value={form.data_aquisicao} onChange={(e) => upd('data_aquisicao', e.target.value)} /></Campo>
        <Campo label="Valor pago (R$)"><input className="input" inputMode="decimal" disabled={!editar} value={form.valor_pago} onChange={(e) => upd('valor_pago', e.target.value)} /></Campo>
        <Campo label="Impostos (R$)"><input className="input" disabled={!editar || comItens} value={form.impostos} onChange={(e) => upd('impostos', e.target.value)} title={comItens ? 'Calculado pelos itens detalhados' : ''} /></Campo>
        <Campo label="Taxas (R$)"><input className="input" disabled={!editar || comItens} value={form.taxas} onChange={(e) => upd('taxas', e.target.value)} /></Campo>
        <Campo label="Despesas diversas (R$)"><input className="input" disabled={!editar || comItens} value={form.despesas_diversas} onChange={(e) => upd('despesas_diversas', e.target.value)} /></Campo>
        <Campo label="Forma de pagamento" className="md:col-span-2"><input className="input" disabled={!editar} value={form.forma_pagamento} onChange={(e) => upd('forma_pagamento', e.target.value)} /></Campo>
        <div className="rounded-md bg-brand-50 p-3"><p className="text-xs uppercase text-brand-700">Total de aquisição</p><p className="text-xl font-bold text-brand-900">{brl(a?.total_aquisicao ?? 0)}</p></div>
      </div>
      {comItens && <p className="mt-2 text-xs text-slate-500">Impostos, taxas e despesas são somados dos itens detalhados abaixo.</p>}
      {editar && <button className="btn-primary mt-3" disabled={salvar.pendente || !form.data_aquisicao} onClick={() => salvar.rodar()}>Salvar aquisição</button>}
      {a && (
        <div className="mt-5"><h3 className="mb-2 text-sm font-semibold">Itens detalhados</h3>
          {a.itens.length ? <table className="w-full text-sm"><tbody className="divide-y">{a.itens.map((i) => (
            <tr key={i.id}><td className="py-1.5"><Badge>{rotulo(i.tipo)}</Badge></td><td>{i.descricao}</td><td>{data(i.data_pagamento)}</td><td className="text-right">{brl(i.valor)}</td>
              <td className="w-8 text-right">{editar && <button onClick={() => delItem.rodar(i.id)} aria-label="Remover"><Trash2 size={14} /></button>}</td></tr>))}</tbody></table> : <p className="text-sm text-slate-500">Sem itens detalhados.</p>}
          {editar && <div className="mt-2 grid items-end gap-2 md:grid-cols-5">
            <Select value={item.tipo} onChange={(e) => setItem({ ...item, tipo: e.target.value })} opcoes={TIPO_ITEM_AQUISICAO.map((t) => ({ v: t, t: rotulo(t) }))} />
            <input className="input md:col-span-2" placeholder="Descrição (ex.: ITBI)" value={item.descricao} onChange={(e) => setItem({ ...item, descricao: e.target.value })} />
            <input className="input" placeholder="Valor" inputMode="decimal" value={item.valor} onChange={(e) => setItem({ ...item, valor: e.target.value })} />
            <button className="btn-ghost" disabled={!item.descricao || !item.valor || addItem.pendente} onClick={() => addItem.rodar()}>Adicionar item</button></div>}
        </div>)}
    </section>
  );
}
/** Mutação mínima com mensagem de erro; evita repetir boilerplate de useMutation neste arquivo. */
function useMutationSimple<A = void>(fn: (arg: A) => Promise<unknown>, ok: () => void) {
  const m = useMutation({ mutationFn: fn, onSuccess: ok });
  return { rodar: (a?: A) => m.mutate(a as A), pendente: m.isPending, erro: m.error ? msgErro(m.error) : null };
}

/* ------------------------------ Despesas ------------------------------ */
interface Item { despesa_id: string; lancamento_id: string; descricao: string; categoria: string; vencimento: string; valor: string; status: string | null; data_pagamento: string | null; justificativa: string | null }
interface Checklist { mes: string; resumo: { pago: number; nao_pago: number; nao_se_aplica: number; a_marcar: number }; itens: Item[] }
function Despesas({ imovelId, editar }: { imovelId: string; editar: boolean }) {
  const qc = useQueryClient(); const [mes, setMes] = useState(mesAtual()); const [modal, setModal] = useState<{ item: Item } | 'nova' | null>(null);
  const chk = useQuery({ queryKey: ['checklist', imovelId, mes], queryFn: () => api.get<Checklist>(`/imoveis/${imovelId}/despesas/checklist?mes=${mes}`) });
  const inval = () => { qc.invalidateQueries({ queryKey: ['checklist', imovelId] }); qc.invalidateQueries({ queryKey: ['retorno', imovelId] }); qc.invalidateQueries({ queryKey: ['alertas-contagem'] }); };
  const marcar = useMutationSimple(({ id, corpo }: { id: string; corpo: object }) => api.patch(`/despesas/lancamentos/${id}/status`, corpo), inval);
  return (
    <section className="card"><div className="mb-3 flex flex-wrap items-center justify-between gap-2"><h2 className="font-semibold">Despesas recorrentes (OPEX) — checklist mensal</h2>
      <div className="flex items-center gap-2"><input type="month" className="input w-40" value={mes} onChange={(e) => e.target.value && setMes(e.target.value)} />{editar && <button className="btn-ghost" onClick={() => setModal('nova')}>Cadastrar despesa</button>}</div></div>
      <Erro msg={marcar.erro} />
      {chk.isLoading ? <Carregando /> : chk.data && (<>
        <p className="mb-2 text-xs text-slate-500">Pago: <b>{chk.data.resumo.pago}</b> · Não pago: <b>{chk.data.resumo.nao_pago}</b> · Não se aplica: <b>{chk.data.resumo.nao_se_aplica}</b> · A marcar: <b>{chk.data.resumo.a_marcar}</b></p>
        {!chk.data.itens.length ? <Vazio>Nenhuma despesa prevista para este mês.</Vazio> : (
          <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr>{['Despesa', 'Vencimento', 'Valor', 'Situação', ''].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
            <tbody className="divide-y">{chk.data.itens.map((i) => (
              <tr key={i.lancamento_id}><td className="td"><b>{i.descricao}</b><p className="text-xs text-slate-500">{i.categoria}</p></td><td className="td">{data(i.vencimento)}</td><td className="td">{brl(i.valor)}</td>
                <td className="td">{i.status ? <Badge k={i.status}>{rotulo(i.status)}</Badge> : <span className="text-slate-400">a marcar</span>}{i.justificativa && <p className="mt-1 max-w-xs text-xs text-red-700">“{i.justificativa}”</p>}</td>
                <td className="td whitespace-nowrap text-right">{editar && <>
                  <button className="btn-ghost px-2 py-1 text-green-700" onClick={() => setModal({ item: { ...i, status: 'PAGO' } })}>Pago</button>{' '}
                  <button className="btn-ghost px-2 py-1 text-red-700" onClick={() => setModal({ item: { ...i, status: 'NAO_PAGO' } })}>Não pago</button>{' '}
                  <button className="btn-ghost px-2 py-1" onClick={() => marcar.rodar({ id: i.lancamento_id, corpo: { status: 'NAO_SE_APLICA' } })}>N/A</button></>}</td></tr>))}</tbody></table></div>)}
      </>)}
      {modal === 'nova' && <NovaDespesa imovelId={imovelId} onClose={() => setModal(null)} onOk={() => { setModal(null); inval(); }} />}
      {modal && modal !== 'nova' && <StatusModal item={modal.item} onClose={() => setModal(null)} onOk={() => { setModal(null); inval(); }} />}
    </section>
  );
}
function StatusModal({ item, onClose, onOk }: { item: Item; onClose: () => void; onOk: () => void }) {
  const naoPago = item.status === 'NAO_PAGO';
  const [just, setJust] = useState(''); const [dt, setDt] = useState(new Date().toISOString().slice(0, 10)); const [valor, setValor] = useState(item.valor ?? '');
  const [campos, setCampos] = useState<Record<string, string[]>>({});
  const m = useMutation({
    mutationFn: () => api.patch(`/despesas/lancamentos/${item.lancamento_id}/status`, naoPago ? { status: 'NAO_PAGO', justificativa: just } : { status: 'PAGO', data_pagamento: dt, valor: num(valor) }),
    onSuccess: onOk, onError: (e) => { if (e instanceof ApiError) setCampos(e.campos); },
  });
  return (
    <Modal titulo={`${naoPago ? 'Marcar como NÃO PAGO' : 'Marcar como PAGO'} — ${item.descricao}`} onClose={onClose}>
      <form className="space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}>
        <Erro msg={m.error && !Object.keys(campos).length ? msgErro(m.error) : null} />
        {naoPago ? <Campo label="Justificativa (obrigatória)" erro={campos.justificativa?.[0]}><textarea className="input" rows={3} required minLength={10} autoFocus placeholder="Explique por que esta despesa não foi paga (mín. 10 caracteres)" value={just} onChange={(e) => setJust(e.target.value)} /></Campo> : (
          <div className="grid grid-cols-2 gap-3"><Campo label="Data do pagamento"><input type="date" className="input" required value={dt} onChange={(e) => setDt(e.target.value)} /></Campo>
            <Campo label="Valor pago (R$)"><input className="input" inputMode="decimal" value={valor} onChange={(e) => setValor(e.target.value)} /></Campo></div>)}
        <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className={naoPago ? 'btn-danger' : 'btn-primary'} disabled={m.isPending}>Confirmar</button></div>
      </form>
    </Modal>
  );
}
function NovaDespesa({ imovelId, onClose, onOk }: { imovelId: string; onClose: () => void; onOk: () => void }) {
  const cats = useQuery({ queryKey: ['categorias-despesa'], queryFn: () => api.get<{ id: string; nome: string }[]>('/categorias-despesa') });
  const [f, setF] = useState({ categoria_id: '', descricao: '', recorrencia: 'MENSAL', dia_vencimento: '10', valor_previsto: '', inicio_em: new Date().toISOString().slice(0, 10), fim_em: '' });
  const m = useMutation({ mutationFn: () => api.post(`/imoveis/${imovelId}/despesas`, { ...f, categoria_id: f.categoria_id || cats.data?.[0]?.id, dia_vencimento: f.recorrencia === 'UNICA' && !f.dia_vencimento ? null : Number(f.dia_vencimento), valor_previsto: num(f.valor_previsto), fim_em: f.fim_em || null }), onSuccess: onOk });
  const set = (k: string, v: string) => setF((s) => ({ ...s, [k]: v }));
  return (
    <Modal titulo="Cadastrar despesa" onClose={onClose}>
      <form className="space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}>
        <Erro msg={m.error ? msgErro(m.error) : null} />
        <div className="grid grid-cols-2 gap-3">
          <Campo label="Categoria"><Select value={f.categoria_id} onChange={(e) => set('categoria_id', e.target.value)} opcoes={(cats.data ?? []).map((c) => ({ v: c.id, t: c.nome }))} /></Campo>
          <Campo label="Recorrência"><Select value={f.recorrencia} onChange={(e) => set('recorrencia', e.target.value)} opcoes={RECORRENCIA.map((r) => ({ v: r, t: rotulo(r) }))} /></Campo>
          <Campo label="Descrição" className="col-span-2"><input className="input" required value={f.descricao} onChange={(e) => set('descricao', e.target.value)} /></Campo>
          <Campo label="Valor previsto (R$)"><input className="input" required inputMode="decimal" value={f.valor_previsto} onChange={(e) => set('valor_previsto', e.target.value)} /></Campo>
          <Campo label="Dia de vencimento"><input className="input" type="number" min={1} max={31} value={f.dia_vencimento} onChange={(e) => set('dia_vencimento', e.target.value)} /></Campo>
          <Campo label="Início"><input type="date" className="input" required value={f.inicio_em} onChange={(e) => set('inicio_em', e.target.value)} /></Campo>
          <Campo label="Fim (opcional)"><input type="date" className="input" value={f.fim_em} onChange={(e) => set('fim_em', e.target.value)} /></Campo>
        </div>
        <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-primary" disabled={m.isPending}>Salvar</button></div>
      </form>
    </Modal>
  );
}

/* ------------------------------ Receitas ------------------------------ */
interface Rec { id: string; tipo: string; data: string; valor_bruto: string; custos_venda: string; tributos: string; valor_liquido: string }
function Receitas({ imovelId, editar }: { imovelId: string; editar: boolean }) {
  const qc = useQueryClient(); const q = useQuery({ queryKey: ['receitas', imovelId], queryFn: () => api.get<Rec[]>(`/imoveis/${imovelId}/receitas`) });
  const [f, setF] = useState({ tipo: 'VENDA', data: new Date().toISOString().slice(0, 10), valor_bruto: '', custos_venda: '0', tributos: '0' });
  const inval = () => { qc.invalidateQueries({ queryKey: ['receitas', imovelId] }); qc.invalidateQueries({ queryKey: ['retorno', imovelId] }); };
  const add = useMutationSimple(() => api.post(`/imoveis/${imovelId}/receitas`, { ...f, valor_bruto: num(f.valor_bruto), custos_venda: num(f.custos_venda || '0'), tributos: num(f.tributos || '0') }), () => { setF({ ...f, valor_bruto: '' }); inval(); });
  const del = useMutationSimple((id: string) => api.del(`/receitas/${id}`), inval);
  return (
    <section className="card"><h2 className="mb-3 font-semibold">Receitas (venda / locação)</h2><Erro msg={add.erro ?? del.erro} />
      {q.data?.length ? <table className="mb-3 w-full text-sm"><thead><tr>{['Tipo', 'Data', 'Bruto', 'Custos', 'Tributos', 'Líquido', ''].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
        <tbody className="divide-y">{q.data.map((r) => <tr key={r.id}><td className="td">{rotulo(r.tipo)}</td><td className="td">{data(r.data)}</td><td className="td">{brl(r.valor_bruto)}</td><td className="td">{brl(r.custos_venda)}</td><td className="td">{brl(r.tributos)}</td><td className="td font-semibold">{brl(r.valor_liquido)}</td>
          <td className="td">{editar && <button onClick={() => confirm('Excluir receita?') && del.rodar(r.id)} aria-label="Excluir"><Trash2 size={14} /></button>}</td></tr>)}</tbody></table> : <p className="mb-3 text-sm text-slate-500">Nenhuma receita lançada.</p>}
      {editar && <div className="grid items-end gap-2 md:grid-cols-6">
        <Select value={f.tipo} onChange={(e) => setF({ ...f, tipo: e.target.value })} opcoes={TIPO_RECEITA.map((t) => ({ v: t, t: rotulo(t) }))} />
        <input type="date" className="input" value={f.data} onChange={(e) => setF({ ...f, data: e.target.value })} />
        <input className="input" placeholder="Valor bruto" inputMode="decimal" value={f.valor_bruto} onChange={(e) => setF({ ...f, valor_bruto: e.target.value })} />
        <input className="input" placeholder="Custos da venda" inputMode="decimal" value={f.custos_venda} onChange={(e) => setF({ ...f, custos_venda: e.target.value })} />
        <input className="input" placeholder="Tributos" inputMode="decimal" value={f.tributos} onChange={(e) => setF({ ...f, tributos: e.target.value })} />
        <button className="btn-primary" disabled={!f.valor_bruto || add.pendente} onClick={() => add.rodar()}>Lançar</button></div>}
    </section>
  );
}

/* ------------------------------ Retorno ------------------------------ */
interface Ret { custo_aquisicao: string; custo_total: string; passivo_pendente: string; custo_corrigido: string | null; receita_liquida: string; resultado: string; roi: number | null; roi_real: number | null; tir_anual: number | null; moic: number | null; prazo_dias: number | null; avisos: string[]; fluxo: { data: string; tipo: string; descricao: string; valor: string; valor_corrigido: string | null }[] }
function Retorno({ imovelId, indice, setIndice }: { imovelId: string; indice: string; setIndice: (i: string) => void }) {
  const q = useQuery({ queryKey: ['retorno', imovelId, indice], queryFn: () => api.get<Ret>(`/imoveis/${imovelId}/retorno?indice=${indice}`) });
  const r = q.data;
  const K = ({ t, v, d }: { t: string; v: string; d?: string }) => <div className="rounded-md border p-3"><p className="text-xs uppercase text-slate-500">{t}</p><p className="text-lg font-semibold">{v}</p>{d && <p className="text-xs text-slate-400">{d}</p>}</div>;
  return (
    <section className="card"><div className="mb-3 flex items-center justify-between"><h2 className="font-semibold">Retorno do investimento</h2>
      <div className="flex items-center gap-2 text-sm">Índice de correção <div className="w-28"><Select value={indice} onChange={(e) => setIndice(e.target.value)} opcoes={CODIGO_INDICE.map((c) => ({ v: c, t: c }))} /></div></div></div>
      {q.isLoading || !r ? <Carregando /> : (<>
        <Erro msg={q.error ? msgErro(q.error) : null} />
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <K t="Custo total" v={brl(r.custo_total)} d={`aquisição ${brl(r.custo_aquisicao)}`} /><K t={`Custo corrigido (${indice})`} v={brl(r.custo_corrigido)} />
          <K t="Receita líquida" v={brl(r.receita_liquida)} /><K t="Resultado" v={brl(r.resultado)} />
          <K t="ROI" v={pct(r.roi)} /><K t="ROI real (descontada a inflação)" v={pct(r.roi_real)} /><K t="TIR anual" v={pct(r.tir_anual)} /><K t="MOIC" v={r.moic === null ? '—' : `${r.moic.toFixed(2)}x`} d={r.prazo_dias ? `${r.prazo_dias} dias` : undefined} />
        </div>
        {Number(r.passivo_pendente) > 0 && <p className="mt-3 rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800">Passivo pendente (despesas não pagas, fora do custo): <b>{brl(r.passivo_pendente)}</b></p>}
        {r.avisos.map((a) => <p key={a} className="mt-2 text-xs text-slate-500">⚠ {a}</p>)}
        {!!r.fluxo.length && <table className="mt-4 w-full text-sm"><thead><tr>{['Data', 'Movimento', 'Valor', 'Corrigido'].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
          <tbody className="divide-y">{r.fluxo.map((f, i) => <tr key={i}><td className="td">{data(f.data)}</td><td className="td">{f.descricao}</td><td className={`td ${f.tipo === 'SAIDA' ? 'text-red-700' : 'text-green-700'}`}>{brl(f.valor)}</td><td className="td">{brl(f.valor_corrigido)}</td></tr>)}</tbody></table>}
      </>)}
    </section>
  );
}
