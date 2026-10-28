'use client';
import { useState } from 'react';
import { DndContext, DragOverlay, PointerSensor, useDraggable, useDroppable, useSensor, useSensors, type DragEndEvent } from '@dnd-kit/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { CANAL_INTERACAO } from '@inovabens/shared';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { brl, dataHora, rotulo } from '@/lib/fmt';
import { Campo, Carregando, Erro, Modal, Select, Titulo } from '@/components/ui';

interface Lead { id: string; pessoa: { nome_razao: string; telefone: string | null; email: string | null }; origem: string | null; responsavel: { nome: string } | null; orcamento_max: string | null; proxima_acao_em: string | null }
interface Estagio { id: string; nome: string; cor: string; tipo: string; leads: Lead[] }
interface Pipeline { funil: string; estagios: Estagio[] }

function Cartao({ lead, onAbrir }: { lead: Lead; onAbrir: () => void }) {
  const { attributes, listeners, setNodeRef, isDragging } = useDraggable({ id: lead.id });
  return (
    <div ref={setNodeRef} {...listeners} {...attributes} onDoubleClick={onAbrir} className={`cursor-grab rounded-md border bg-white p-2.5 text-sm shadow-sm ${isDragging ? 'opacity-30' : ''}`}>
      <p className="font-medium">{lead.pessoa.nome_razao}</p>
      <p className="text-xs text-slate-500">{lead.pessoa.telefone ?? lead.pessoa.email ?? ''}</p>
      {lead.orcamento_max && <p className="text-xs">Até {brl(lead.orcamento_max)}</p>}
      <div className="mt-1 flex justify-between text-xs text-slate-400"><span>{lead.responsavel?.nome ?? ''}</span><button className="text-brand-600 underline" onPointerDown={(e) => e.stopPropagation()} onClick={onAbrir}>abrir</button></div>
    </div>
  );
}
function Coluna({ e, onAbrir }: { e: Estagio; onAbrir: (l: Lead) => void }) {
  const { setNodeRef, isOver } = useDroppable({ id: e.id });
  return (
    <div ref={setNodeRef} className={`w-64 shrink-0 rounded-lg bg-slate-100 p-2 ${isOver ? 'ring-2 ring-brand-500' : ''}`}>
      <div className="mb-2 flex items-center justify-between px-1 text-sm font-semibold"><span style={{ color: e.cor }}>● {e.nome}</span><span className="text-slate-400">{e.leads.length}</span></div>
      <div className="min-h-[60px] space-y-2">{e.leads.map((l) => <Cartao key={l.id} lead={l} onAbrir={() => onAbrir(l)} />)}</div>
    </div>
  );
}

export default function Crm() {
  const { pode } = useAuth(); const qc = useQueryClient();
  const [funil, setFunil] = useState('VENDA'); const [novo, setNovo] = useState(false); const [aberto, setAberto] = useState<Lead | null>(null);
  const [perda, setPerda] = useState<{ lead: string; estagio: string } | null>(null); const [ativo, setAtivo] = useState<Lead | null>(null);
  const chave = ['pipeline', funil];
  const q = useQuery({ queryKey: chave, queryFn: () => api.get<Pipeline>(`/crm/pipeline?funil=${funil}`) });
  const sensors = useSensors(useSensor(PointerSensor, { activationConstraint: { distance: 6 } }));
  const mover = useMutation({ mutationFn: (p: { id: string; estagio_id: string; motivo_perda?: string }) => api.patch(`/leads/${p.id}/estagio`, { estagio_id: p.estagio_id, motivo_perda: p.motivo_perda }), onSettled: () => qc.invalidateQueries({ queryKey: chave }) });
  function fim(ev: DragEndEvent) {
    setAtivo(null); if (!ev.over || !q.data || !pode('crm:editar')) return;
    const destino = q.data.estagios.find((e) => e.id === ev.over!.id); const origem = q.data.estagios.find((e) => e.leads.some((l) => l.id === ev.active.id));
    if (!destino || origem?.id === destino.id) return;
    if (destino.tipo === 'PERDIDO') setPerda({ lead: String(ev.active.id), estagio: destino.id });
    else mover.mutate({ id: String(ev.active.id), estagio_id: destino.id });
  }
  return (
    <>
      <Titulo acoes={<>
        <div className="w-36"><Select value={funil} onChange={(e) => setFunil(e.target.value)} opcoes={[{ v: 'VENDA', t: 'Funil de venda' }, { v: 'LOCACAO', t: 'Funil de locação' }]} /></div>
        {pode('crm:criar') && <button className="btn-primary" onClick={() => setNovo(true)}><Plus size={16} />Novo lead</button>}</>}>CRM</Titulo>
      <Erro msg={mover.error ? msgErro(mover.error) : null} />
      {q.isLoading || !q.data ? <Carregando /> : (
        <DndContext sensors={sensors} onDragStart={(e) => setAtivo(q.data!.estagios.flatMap((x) => x.leads).find((l) => l.id === e.active.id) ?? null)} onDragEnd={fim} onDragCancel={() => setAtivo(null)}>
          <div className="flex gap-3 overflow-x-auto pb-3">{q.data.estagios.map((e) => <Coluna key={e.id} e={e} onAbrir={setAberto} />)}</div>
          <DragOverlay>{ativo && <div className="rounded-md border bg-white p-2.5 text-sm shadow-lg">{ativo.pessoa.nome_razao}</div>}</DragOverlay>
        </DndContext>)}
      {novo && <NovoLead funil={funil} onClose={() => setNovo(false)} onOk={() => { setNovo(false); qc.invalidateQueries({ queryKey: chave }); }} />}
      {aberto && <Detalhe lead={aberto} onClose={() => setAberto(null)} />}
      {perda && <Perda onClose={() => setPerda(null)} onConfirm={(motivo) => { mover.mutate({ id: perda.lead, estagio_id: perda.estagio, motivo_perda: motivo }); setPerda(null); }} />}
    </>
  );
}
function Perda({ onClose, onConfirm }: { onClose: () => void; onConfirm: (m: string) => void }) {
  const [m, setM] = useState('');
  return <Modal titulo="Motivo da perda" onClose={onClose}><form className="space-y-3" onSubmit={(e) => { e.preventDefault(); onConfirm(m); }}>
    <Campo label="Por que este lead foi perdido?"><textarea className="input" rows={3} required autoFocus value={m} onChange={(e) => setM(e.target.value)} /></Campo>
    <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-danger">Marcar como perdido</button></div></form></Modal>;
}
function NovoLead({ funil, onClose, onOk }: { funil: string; onClose: () => void; onOk: () => void }) {
  const [f, setF] = useState({ nome: '', telefone: '', email: '', origem: '', orcamento_max: '' });
  const m = useMutation({ mutationFn: () => api.post('/leads', { finalidade: funil === 'VENDA' ? 'COMPRA' : 'LOCACAO', origem: f.origem || null, orcamento_max: f.orcamento_max ? f.orcamento_max.replace(',', '.') : null, pessoa: { nome_razao: f.nome, telefone: f.telefone || null, email: f.email || null } }), onSuccess: onOk });
  const set = (k: string, v: string) => setF((s) => ({ ...s, [k]: v }));
  return <Modal titulo="Novo lead" onClose={onClose}><form className="space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}><Erro msg={m.error ? msgErro(m.error) : null} />
    <Campo label="Nome *"><input className="input" required value={f.nome} onChange={(e) => set('nome', e.target.value)} /></Campo>
    <div className="grid grid-cols-2 gap-3"><Campo label="Telefone"><input className="input" value={f.telefone} onChange={(e) => set('telefone', e.target.value)} /></Campo><Campo label="E-mail"><input className="input" type="email" value={f.email} onChange={(e) => set('email', e.target.value)} /></Campo>
      <Campo label="Origem"><input className="input" placeholder="Indicação, site…" value={f.origem} onChange={(e) => set('origem', e.target.value)} /></Campo><Campo label="Orçamento máx. (R$)"><input className="input" inputMode="decimal" value={f.orcamento_max} onChange={(e) => set('orcamento_max', e.target.value)} /></Campo></div>
    <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-primary" disabled={m.isPending}>Criar lead</button></div></form></Modal>;
}
function Detalhe({ lead, onClose }: { lead: Lead; onClose: () => void }) {
  const qc = useQueryClient(); const { pode } = useAuth();
  const inter = useQuery({ queryKey: ['interacoes', lead.id], queryFn: () => api.get<{ id: string; canal: string; resumo: string; criado_em: string }[]>(`/leads/${lead.id}/interacoes`) });
  const hist = useQuery({ queryKey: ['historico', lead.id], queryFn: () => api.get<{ id: string; criado_em: string; estagio_anterior: string | null; estagio_novo: string; usuario: string | null }[]>(`/leads/${lead.id}/historico`) });
  const [canal, setCanal] = useState('WHATSAPP'); const [resumo, setResumo] = useState('');
  const add = useMutation({ mutationFn: () => api.post(`/leads/${lead.id}/interacoes`, { canal, resumo }), onSuccess: () => { setResumo(''); qc.invalidateQueries({ queryKey: ['interacoes', lead.id] }); } });
  return (
    <Modal titulo={lead.pessoa.nome_razao} onClose={onClose} largo>
      <p className="mb-3 text-sm text-slate-500">{[lead.pessoa.telefone, lead.pessoa.email, lead.origem].filter(Boolean).join(' · ')}</p>
      <div className="grid gap-4 md:grid-cols-2">
        <div><h3 className="mb-2 font-semibold">Interações</h3><Erro msg={add.error ? msgErro(add.error) : null} />
          {pode('crm:editar') && <div className="mb-2 flex gap-2"><div className="w-32"><Select value={canal} onChange={(e) => setCanal(e.target.value)} opcoes={CANAL_INTERACAO.map((c) => ({ v: c, t: rotulo(c) }))} /></div>
            <input className="input" placeholder="Resumo do contato" value={resumo} onChange={(e) => setResumo(e.target.value)} /><button className="btn-primary" disabled={resumo.length < 2} onClick={() => add.mutate()}>+</button></div>}
          <ul className="space-y-1 text-sm">{inter.data?.map((i) => <li key={i.id} className="rounded border p-2"><b>{rotulo(i.canal)}</b> · <span className="text-xs text-slate-500">{dataHora(i.criado_em)}</span><p>{i.resumo}</p></li>)}{!inter.data?.length && <li className="text-slate-400">Nenhuma interação.</li>}</ul></div>
        <div><h3 className="mb-2 font-semibold">Histórico de estágios</h3>
          <ul className="space-y-1 text-sm">{hist.data?.map((h) => <li key={h.id} className="rounded border p-2">{h.estagio_anterior ?? '—'} → <b>{h.estagio_novo}</b><p className="text-xs text-slate-500">{dataHora(h.criado_em)} · {h.usuario}</p></li>)}</ul></div>
      </div>
    </Modal>
  );
}
