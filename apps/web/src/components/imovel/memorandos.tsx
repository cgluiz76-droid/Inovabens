'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pin } from 'lucide-react';
import { TIPO_MEMORANDO } from '@inovabens/shared';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { dataHora, rotulo } from '@/lib/fmt';
import { Badge, Campo, Carregando, Erro, Select, Vazio } from '../ui';

interface Memo { id: string; tipo: string; titulo: string; corpo: string | null; fixado: boolean; criado_em: string; autor: { nome: string } | null }
export default function Memorandos({ imovelId }: { imovelId: string }) {
  const { pode } = useAuth(); const qc = useQueryClient(); const chave = ['memorandos', imovelId];
  const { data, isLoading } = useQuery({ queryKey: chave, queryFn: () => api.get<Memo[]>(`/imoveis/${imovelId}/memorandos`) });
  const [f, setF] = useState({ tipo: 'MEMORANDO', titulo: '', corpo: '' });
  const criar = useMutation({ mutationFn: () => api.post(`/imoveis/${imovelId}/memorandos`, { ...f, corpo: f.corpo || null }), onSuccess: () => { setF({ tipo: 'MEMORANDO', titulo: '', corpo: '' }); qc.invalidateQueries({ queryKey: chave }); } });
  const fixar = useMutation({ mutationFn: (m: Memo) => api.patch(`/memorandos/${m.id}`, { fixado: !m.fixado }), onSuccess: () => qc.invalidateQueries({ queryKey: chave }) });
  const excluir = useMutation({ mutationFn: (id: string) => api.del(`/memorandos/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: chave }) });
  return (
    <div className="space-y-4">
      {pode('memorandos:criar') && (
        <form className="card space-y-3" onSubmit={(e) => { e.preventDefault(); criar.mutate(); }}>
          <Erro msg={criar.error ? msgErro(criar.error) : null} />
          <div className="grid gap-3 md:grid-cols-4"><Campo label="Tipo"><Select value={f.tipo} onChange={(e) => setF({ ...f, tipo: e.target.value })} opcoes={TIPO_MEMORANDO.map((t) => ({ v: t, t: rotulo(t) }))} /></Campo>
            <Campo label="Título" className="md:col-span-3"><input className="input" required value={f.titulo} onChange={(e) => setF({ ...f, titulo: e.target.value })} /></Campo></div>
          <Campo label="Texto"><textarea className="input" rows={3} value={f.corpo} onChange={(e) => setF({ ...f, corpo: e.target.value })} /></Campo>
          <button className="btn-primary" disabled={criar.isPending}>Registrar memorando</button>
        </form>)}
      {isLoading ? <Carregando /> : !data?.length ? <Vazio>Nenhum memorando.</Vazio> : data.map((m) => (
        <article key={m.id} className="card">
          <div className="flex items-start justify-between gap-2"><div><Badge>{rotulo(m.tipo)}</Badge> <b className="ml-1">{m.titulo}</b><p className="text-xs text-slate-500">{m.autor?.nome} · {dataHora(m.criado_em)}</p></div>
            <div className="flex gap-1">{pode('memorandos:editar') && <button className={m.fixado ? 'btn-primary' : 'btn-ghost'} onClick={() => fixar.mutate(m)} aria-label="Fixar"><Pin size={14} /></button>}
              {pode('memorandos:excluir') && <button className="btn-ghost" onClick={() => confirm('Excluir memorando?') && excluir.mutate(m.id)}>Excluir</button>}</div></div>
          {m.corpo && <p className="mt-2 whitespace-pre-wrap text-sm">{m.corpo}</p>}
        </article>))}
    </div>
  );
}
