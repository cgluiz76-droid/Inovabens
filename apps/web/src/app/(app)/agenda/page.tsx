'use client';
import { Suspense, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import FullCalendar from '@fullcalendar/react';
import dayGridPlugin from '@fullcalendar/daygrid';
import timeGridPlugin from '@fullcalendar/timegrid';
import interactionPlugin from '@fullcalendar/interaction';
import type { DatesSetArg, EventClickArg } from '@fullcalendar/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { dataHora } from '@/lib/fmt';
import { FormCompromisso, type Compromisso } from '@/components/imovel/agenda-imovel';
import { Badge, Campo, Erro, Modal, Select, Titulo } from '@/components/ui';

interface GStatus { conectado: boolean; google_email: string | null; ultima_sync_em: string | null }
function Conteudo() {
  const { pode } = useAuth(); const qc = useQueryClient(); const sp = useSearchParams();
  const [faixa, setFaixa] = useState<{ de: string; ate: string } | null>(null); const [sel, setSel] = useState<Compromisso | null>(null); const [novo, setNovo] = useState(false); const [imovelId, setImovelId] = useState('');
  const lista = useQuery({ queryKey: ['compromissos', faixa], enabled: !!faixa, queryFn: () => api.get<Compromisso[]>(`/compromissos?de=${faixa!.de}&ate=${faixa!.ate}`) });
  const imoveis = useQuery({ queryKey: ['imoveis-opcoes'], queryFn: async () => (await api.get<{ data: { id: string; codigo: string; titulo: string }[] }>('/imoveis?pageSize=100')).data });
  const g = useQuery({ queryKey: ['google'], queryFn: () => api.get<GStatus>('/integracoes/google/status') });
  const inval = () => { qc.invalidateQueries({ queryKey: ['compromissos'] }); qc.invalidateQueries({ queryKey: ['alertas-contagem'] }); };
  const conectar = useMutation({ mutationFn: async () => { const r = await api.get<{ url: string }>('/integracoes/google/auth-url'); window.location.href = r.url; } });
  const sync = useMutation({ mutationFn: () => api.post('/integracoes/google/sync'), onSuccess: () => { qc.invalidateQueries({ queryKey: ['google'] }); inval(); } });
  const desconectar = useMutation({ mutationFn: () => api.del('/integracoes/google'), onSuccess: () => qc.invalidateQueries({ queryKey: ['google'] }) });
  const concluir = useMutation({ mutationFn: (id: string) => api.post(`/compromissos/${id}/concluir`), onSuccess: () => { setSel(null); inval(); } });
  const eventos = (lista.data ?? []).map((c) => {
    const vencido = c.status === 'PENDENTE' && new Date(c.fim_em) < new Date();
    return { id: c.id, title: `${c.imovel?.codigo ?? ''} ${c.titulo}`.trim(), start: c.inicio_em, end: c.fim_em, backgroundColor: c.status === 'CONCLUIDO' ? '#16a34a' : c.status === 'CANCELADO' ? '#94a3b8' : vencido ? '#dc2626' : '#186356', borderColor: 'transparent' };
  });
  return (
    <>
      <Titulo acoes={pode('agenda:criar') && <button className="btn-primary" onClick={() => setNovo(true)}>Novo compromisso</button>}>Agenda</Titulo>
      {sp.get('google') === 'ok' && <p className="mb-3 rounded-md bg-green-50 px-3 py-2 text-sm text-green-700">Google Calendar conectado.</p>}
      <div className="card mb-4 flex flex-wrap items-center justify-between gap-2 text-sm">
        <div><b>Google Calendar</b>{g.data?.conectado ? <span> · conectado como {g.data.google_email} · última sincronização {dataHora(g.data.ultima_sync_em)}</span> : <span className="text-slate-500"> · não conectado (compromissos ficam só no sistema)</span>}</div>
        <div className="flex gap-2">{g.data?.conectado ? <><button className="btn-ghost" onClick={() => sync.mutate()} disabled={sync.isPending}>{sync.isPending ? 'Sincronizando…' : 'Sincronizar agora'}</button><button className="btn-ghost" onClick={() => confirm('Desconectar o Google Calendar?') && desconectar.mutate()}>Desconectar</button></> : <button className="btn-primary" onClick={() => conectar.mutate()}>Conectar Google Calendar</button>}</div>
      </div>
      <Erro msg={(sync.error ?? conectar.error ?? concluir.error) ? msgErro(sync.error ?? conectar.error ?? concluir.error) : null} />
      <div className="card">
        <FullCalendar plugins={[dayGridPlugin, timeGridPlugin, interactionPlugin]} initialView="timeGridWeek" locale="pt-br" height="auto" nowIndicator events={eventos}
          headerToolbar={{ left: 'prev,next today', center: 'title', right: 'dayGridMonth,timeGridWeek,timeGridDay' }} buttonText={{ today: 'Hoje', month: 'Mês', week: 'Semana', day: 'Dia' }}
          datesSet={(a: DatesSetArg) => setFaixa({ de: a.start.toISOString(), ate: a.end.toISOString() })}
          eventClick={(a: EventClickArg) => setSel(lista.data?.find((c) => c.id === a.event.id) ?? null)} />
      </div>
      {sel && (
        <Modal titulo={sel.titulo} onClose={() => setSel(null)}>
          <div className="space-y-2 text-sm"><p>{dataHora(sel.inicio_em)} → {dataHora(sel.fim_em)}</p>{sel.local && <p>📍 {sel.local}</p>}{sel.descricao && <p>{sel.descricao}</p>}
            <p>{sel.imovel && <a className="text-brand-600 underline" href={`/imoveis/${sel.imovel.id}?aba=agenda`}>{sel.imovel.codigo} — {sel.imovel.titulo}</a>} <Badge k={sel.status}>{sel.status}</Badge></p>
            {sel.status === 'PENDENTE' && pode('agenda:editar') && <button className="btn-primary" onClick={() => concluir.mutate(sel.id)}>Marcar como concluído</button>}</div>
        </Modal>)}
      {novo && (
        <Modal titulo="Novo compromisso" onClose={() => setNovo(false)} largo>
          <Campo label="Imóvel" className="mb-3"><Select value={imovelId} onChange={(e) => setImovelId(e.target.value)} opcoes={[{ v: '', t: 'Selecione o imóvel…' }, ...(imoveis.data ?? []).map((i) => ({ v: i.id, t: `${i.codigo} — ${i.titulo}` }))]} /></Campo>
          {imovelId ? <FormCompromisso imovelId={imovelId} onOk={() => { setNovo(false); inval(); }} /> : <p className="text-sm text-slate-500">Todo compromisso pertence a um imóvel.</p>}
        </Modal>)}
    </>
  );
}
export default function Agenda() { return <Suspense><Conteudo /></Suspense>; }
