'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { STATUS_REGULARIZACAO, TIPO_REGULARIZACAO } from '@inovabens/shared';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { brl, data, rotulo } from '@/lib/fmt';
import { Badge, Campo, Carregando, Erro, Select, Vazio } from '../ui';

interface Reg { id: string; tipo: string; status: string; custo_estimado: string | null; prazo_previsto: string | null; observacao: string | null; profissional: { nome_razao: string } | null }
export default function Regularizacao({ imovelId }: { imovelId: string }) {
  const { pode } = useAuth(); const qc = useQueryClient(); const chave = ['regularizacoes', imovelId];
  const lista = useQuery({ queryKey: chave, queryFn: () => api.get<Reg[]>(`/imoveis/${imovelId}/regularizacoes`) });
  const pessoas = useQuery({ queryKey: ['prestadores'], queryFn: async () => (await api.get<{ data: { id: string; nome_razao: string }[] }>('/pessoas?papel=PRESTADOR&pageSize=100')).data, enabled: pode('pessoas:ver') });
  const [f, setF] = useState({ tipo: 'DOCUMENTACAO', profissional_id: '', custo_estimado: '', prazo_previsto: '', observacao: '' });
  const criar = useMutation({
    mutationFn: () => api.post(`/imoveis/${imovelId}/regularizacoes`, Object.fromEntries(Object.entries(f).map(([k, v]) => [k, v === '' ? null : v]))),
    onSuccess: () => { setF({ tipo: 'DOCUMENTACAO', profissional_id: '', custo_estimado: '', prazo_previsto: '', observacao: '' }); qc.invalidateQueries({ queryKey: chave }); },
  });
  const status = useMutation({ mutationFn: ({ id, s }: { id: string; s: string }) => api.patch(`/regularizacoes/${id}`, { status: s }), onSuccess: () => qc.invalidateQueries({ queryKey: chave }) });
  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-500">Processos para sanear o imóvel: documentação, tributos em atraso, desmembramento, fracionamento ou retificação de área.</p>
      {pode('imoveis:editar') && (
        <form className="card space-y-3" onSubmit={(e) => { e.preventDefault(); criar.mutate(); }}>
          <Erro msg={criar.error ? msgErro(criar.error) : null} />
          <div className="grid gap-3 md:grid-cols-4">
            <Campo label="Tipo"><Select value={f.tipo} onChange={(e) => setF({ ...f, tipo: e.target.value })} opcoes={TIPO_REGULARIZACAO.map((t) => ({ v: t, t: rotulo(t) }))} /></Campo>
            <Campo label="Profissional"><Select value={f.profissional_id} onChange={(e) => setF({ ...f, profissional_id: e.target.value })} opcoes={[{ v: '', t: '—' }, ...(pessoas.data ?? []).map((p) => ({ v: p.id, t: p.nome_razao }))]} /></Campo>
            <Campo label="Custo estimado (R$)"><input className="input" inputMode="decimal" value={f.custo_estimado} onChange={(e) => setF({ ...f, custo_estimado: e.target.value.replace(',', '.') })} /></Campo>
            <Campo label="Prazo previsto"><input type="date" className="input" value={f.prazo_previsto} onChange={(e) => setF({ ...f, prazo_previsto: e.target.value })} /></Campo>
          </div>
          <Campo label="Observações"><input className="input" value={f.observacao} onChange={(e) => setF({ ...f, observacao: e.target.value })} /></Campo>
          <button className="btn-primary" disabled={criar.isPending}>Adicionar processo</button>
        </form>)}
      {lista.isLoading ? <Carregando /> : !lista.data?.length ? <Vazio>Nenhum processo de regularização.</Vazio> : lista.data.map((r) => (
        <div key={r.id} className="card flex flex-wrap items-center justify-between gap-2">
          <div><b>{rotulo(r.tipo)}</b> <Badge>{rotulo(r.status)}</Badge>
            <p className="text-xs text-slate-500">{r.profissional?.nome_razao ?? 'Sem profissional'} · custo estimado {brl(r.custo_estimado)} · prazo {data(r.prazo_previsto)}</p>{r.observacao && <p className="text-sm">{r.observacao}</p>}</div>
          {pode('imoveis:editar') && <div className="w-44"><Select value={r.status} onChange={(e) => status.mutate({ id: r.id, s: e.target.value })} opcoes={STATUS_REGULARIZACAO.map((s) => ({ v: s, t: rotulo(s) }))} /></div>}
        </div>))}
    </div>
  );
}
