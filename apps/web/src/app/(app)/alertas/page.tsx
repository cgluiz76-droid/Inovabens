'use client';
import Link from 'next/link';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, msgErro } from '@/lib/api';
import { dataHora, paraInputLocal } from '@/lib/fmt';
import { Badge, Carregando, Campo, Erro, Modal, Titulo, Vazio } from '@/components/ui';

interface Alerta { id: string; tipo: string; mensagem: string; gerado_em: string; lido_em: string | null; resolvido_em: string | null; compromisso?: { id: string; titulo: string; fim_em: string; imovel_id: string } | null }
export default function Alertas() {
  const qc = useQueryClient(); const [todos, setTodos] = useState(false); const [re, setRe] = useState<Alerta | null>(null);
  const q = useQuery({ queryKey: ['alertas', todos], queryFn: () => api.get<Alerta[]>(`/alertas?status=${todos ? 'todos' : 'aberto'}`) });
  const inval = () => { qc.invalidateQueries({ queryKey: ['alertas'] }); qc.invalidateQueries({ queryKey: ['alertas-contagem'] }); };
  const lido = useMutation({ mutationFn: (id: string) => api.patch(`/alertas/${id}/lido`), onSuccess: inval });
  const concluir = useMutation({ mutationFn: (id: string) => api.post(`/compromissos/${id}/concluir`), onSuccess: inval });
  return (
    <>
      <Titulo acoes={<label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={todos} onChange={(e) => setTodos(e.target.checked)} />Mostrar resolvidos</label>}>Alertas</Titulo>
      {q.isLoading ? <Carregando /> : !q.data?.length ? <Vazio>Nenhum alerta.</Vazio> : (
        <div className="space-y-2">{q.data.map((a) => (
          <div key={a.id} className="card flex flex-wrap items-center justify-between gap-2">
            <div><Badge k={a.resolvido_em ? 'CONCLUIDO' : 'NAO_PAGO'}>{a.tipo === 'DESPESA_NAO_PAGA' ? 'Despesa' : 'Compromisso'}</Badge>
              <p className="mt-1 text-sm font-medium">{a.mensagem}</p><p className="text-xs text-slate-500">{dataHora(a.gerado_em)}</p></div>
            {!a.resolvido_em && <div className="flex gap-2">
              {a.compromisso && <><button className="btn-primary" onClick={() => concluir.mutate(a.compromisso!.id)}>Marcar como cumprido</button><button className="btn-ghost" onClick={() => setRe(a)}>Reagendar</button>
                <Link className="btn-ghost" href={`/imoveis/${a.compromisso.imovel_id}?aba=agenda`}>Abrir imóvel</Link></>}
              {a.tipo === 'DESPESA_NAO_PAGA' && <Link className="btn-ghost" href="/imoveis">Ver imóveis</Link>}
              {!a.lido_em && <button className="btn-ghost" onClick={() => lido.mutate(a.id)}>Marcar lido</button>}
            </div>}
          </div>))}</div>)}
      {re && <Reagendar alerta={re} onClose={() => setRe(null)} onOk={() => { setRe(null); inval(); }} />}
    </>
  );
}
function Reagendar({ alerta, onClose, onOk }: { alerta: Alerta; onClose: () => void; onOk: () => void }) {
  const base = new Date(Date.now() + 86400_000).toISOString();
  const [ini, setIni] = useState(paraInputLocal(base)); const [fim, setFim] = useState(paraInputLocal(new Date(Date.now() + 90000_000).toISOString()));
  const m = useMutation({ mutationFn: () => api.patch(`/alertas/${alerta.id}/reagendar`, { inicio_em: new Date(ini).toISOString(), fim_em: new Date(fim).toISOString() }), onSuccess: onOk });
  return (
    <Modal titulo="Reagendar compromisso" onClose={onClose}>
      <div className="space-y-3"><p className="text-sm text-slate-600">{alerta.compromisso?.titulo}</p>
        <Erro msg={m.error ? msgErro(m.error) : null} />
        <Campo label="Início"><input type="datetime-local" className="input" value={ini} onChange={(e) => setIni(e.target.value)} /></Campo>
        <Campo label="Término"><input type="datetime-local" className="input" value={fim} onChange={(e) => setFim(e.target.value)} /></Campo>
        <div className="flex justify-end gap-2"><button className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-primary" onClick={() => m.mutate()} disabled={m.isPending}>Reagendar</button></div></div>
    </Modal>
  );
}
