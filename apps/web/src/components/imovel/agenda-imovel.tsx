'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { TIPO_COMPROMISSO } from '@inovabens/shared';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { dataHora, paraInputLocal, rotulo } from '@/lib/fmt';
import { Badge, Campo, Carregando, Erro, Select, Vazio } from '../ui';

export interface Compromisso { id: string; tipo: string; titulo: string; descricao: string | null; local: string | null; inicio_em: string; fim_em: string; status: string; responsavel?: { nome: string } | null; imovel?: { id: string; codigo: string; titulo: string } }

export function FormCompromisso({ imovelId, inicial, onOk }: { imovelId: string; inicial?: { inicio?: string; fim?: string }; onOk: () => void }) {
  const agora = new Date(); agora.setMinutes(0, 0, 0); const h1 = new Date(agora.getTime() + 3600_000), h2 = new Date(agora.getTime() + 7200_000);
  const [f, setF] = useState({ tipo: 'VISITA', titulo: '', local: '', descricao: '', inicio: paraInputLocal(inicial?.inicio ?? h1.toISOString()), fim: paraInputLocal(inicial?.fim ?? h2.toISOString()), emails: '' });
  const m = useMutation({
    mutationFn: () => api.post(`/imoveis/${imovelId}/compromissos`, { tipo: f.tipo, titulo: f.titulo, local: f.local || null, descricao: f.descricao || null, inicio_em: new Date(f.inicio).toISOString(), fim_em: new Date(f.fim).toISOString(), participantes: f.emails.split(/[,;\s]+/).filter(Boolean).map((email) => ({ email })) }),
    onSuccess: () => { setF({ ...f, titulo: '', local: '', descricao: '', emails: '' }); onOk(); },
  });
  const set = (k: string, v: string) => setF((s) => ({ ...s, [k]: v }));
  return (
    <form className="card space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}>
      <Erro msg={m.error ? msgErro(m.error) : null} />
      <div className="grid gap-3 md:grid-cols-4">
        <Campo label="Tipo"><Select value={f.tipo} onChange={(e) => set('tipo', e.target.value)} opcoes={TIPO_COMPROMISSO.map((t) => ({ v: t, t: rotulo(t) }))} /></Campo>
        <Campo label="Título" className="md:col-span-3"><input className="input" required value={f.titulo} onChange={(e) => set('titulo', e.target.value)} /></Campo>
        <Campo label="Início"><input type="datetime-local" className="input" required value={f.inicio} onChange={(e) => set('inicio', e.target.value)} /></Campo>
        <Campo label="Término"><input type="datetime-local" className="input" required value={f.fim} onChange={(e) => set('fim', e.target.value)} /></Campo>
        <Campo label="Local"><input className="input" value={f.local} onChange={(e) => set('local', e.target.value)} /></Campo>
        <Campo label="Convidados (e-mails)"><input className="input" placeholder="a@x.com, b@y.com" value={f.emails} onChange={(e) => set('emails', e.target.value)} /></Campo>
      </div>
      <button className="btn-primary" disabled={m.isPending}>Agendar</button>
    </form>
  );
}

export default function AgendaImovel({ imovelId }: { imovelId: string }) {
  const { pode } = useAuth(); const qc = useQueryClient(); const chave = ['compromissos-imovel', imovelId];
  const q = useQuery({ queryKey: chave, queryFn: () => api.get<Compromisso[]>(`/imoveis/${imovelId}/compromissos`) });
  const inval = () => { qc.invalidateQueries({ queryKey: chave }); qc.invalidateQueries({ queryKey: ['alertas-contagem'] }); qc.invalidateQueries({ queryKey: ['compromissos'] }); };
  const concluir = useMutation({ mutationFn: (id: string) => api.post(`/compromissos/${id}/concluir`), onSuccess: inval });
  const cancelar = useMutation({ mutationFn: (id: string) => api.patch(`/compromissos/${id}`, { status: 'CANCELADO' }), onSuccess: inval });
  const vencido = (c: Compromisso) => c.status === 'PENDENTE' && new Date(c.fim_em) < new Date();
  return (
    <div className="space-y-4">
      {pode('agenda:criar') && <FormCompromisso imovelId={imovelId} onOk={inval} />}
      {q.isLoading ? <Carregando /> : !q.data?.length ? <Vazio>Nenhum compromisso para este imóvel.</Vazio> : q.data.map((c) => (
        <div key={c.id} className={`card flex flex-wrap items-center justify-between gap-2 ${vencido(c) ? 'border-red-300 bg-red-50' : ''}`}>
          <div><b>{c.titulo}</b> <Badge>{rotulo(c.tipo)}</Badge> <Badge k={c.status}>{rotulo(c.status)}</Badge>{vencido(c) && <Badge k="NAO_PAGO">Não cumprido</Badge>}
            <p className="text-xs text-slate-500">{dataHora(c.inicio_em)} → {dataHora(c.fim_em)}{c.local ? ` · ${c.local}` : ''}{c.responsavel ? ` · ${c.responsavel.nome}` : ''}</p></div>
          {c.status === 'PENDENTE' && pode('agenda:editar') && <div className="flex gap-2"><button className="btn-primary" onClick={() => concluir.mutate(c.id)}>Concluir</button><button className="btn-ghost" onClick={() => confirm('Cancelar compromisso?') && cancelar.mutate(c.id)}>Cancelar</button></div>}
        </div>))}
    </div>
  );
}
