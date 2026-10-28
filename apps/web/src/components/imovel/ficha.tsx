'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Trash2, Upload } from 'lucide-react';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { ImovelForm } from '../imovel-form';
import { Erro } from '../ui';

export interface ImovelDetalhe {
  id: string; codigo: string; titulo: string; [k: string]: unknown;
  caracteristicas: { chave: string; valor: string | null; publico: boolean }[];
  midias: { id: string; url: string | null; tipo: string; legenda: string | null; capa: boolean }[];
  proprietarios: { pessoa_id: string; percentual: string; principal: boolean; pessoa: { nome_razao: string } }[];
  envolvidos: { pessoa_id: string; papel: string; observacao: string | null; pessoa: { nome_razao: string } }[];
}
interface Opcao { id: string; nome_razao: string }
const usePessoas = () => useQuery({ queryKey: ['pessoas-opcoes'], queryFn: async () => (await api.get<{ data: Opcao[] }>('/pessoas?pageSize=100')).data });

export default function Ficha({ imovel }: { imovel: ImovelDetalhe }) {
  const qc = useQueryClient(); const { pode } = useAuth(); const editar = pode('imoveis:editar');
  const recarregar = () => qc.invalidateQueries({ queryKey: ['imovel', imovel.id] });
  return (
    <div className="space-y-6">
      <section className="card"><h2 className="mb-3 font-semibold">Dados do imóvel</h2>
        {editar ? <ImovelForm inicial={imovel as never} onSalvar={async (c) => { await api.patch(`/imoveis/${imovel.id}`, c); recarregar(); }} />
          : <p className="text-sm text-slate-500">Você não tem permissão para editar este imóvel.</p>}</section>
      <Proprietarios imovel={imovel} editar={editar} onSalvo={recarregar} />
      <Caracteristicas imovel={imovel} editar={editar} onSalvo={recarregar} />
      <Midias imovel={imovel} editar={editar} onSalvo={recarregar} />
    </div>
  );
}

function Proprietarios({ imovel, editar, onSalvo }: { imovel: ImovelDetalhe; editar: boolean; onSalvo: () => void }) {
  const pessoas = usePessoas();
  const [itens, setItens] = useState(imovel.proprietarios.map((p) => ({ pessoa_id: p.pessoa_id, percentual: p.percentual, principal: p.principal })));
  const soma = itens.reduce((s, i) => s + Number(i.percentual || 0), 0);
  const m = useMutation({ mutationFn: () => api.put(`/imoveis/${imovel.id}/proprietarios`, itens.map((i) => ({ ...i, percentual: String(i.percentual).replace(',', '.') }))), onSuccess: onSalvo });
  return (
    <section className="card"><h2 className="mb-1 font-semibold">Proprietários</h2><p className="mb-3 text-xs text-slate-500">A soma dos percentuais deve ser 100%.</p>
      <Erro msg={m.error ? msgErro(m.error) : null} />
      {itens.map((it, i) => (
        <div key={i} className="mb-2 flex flex-wrap items-center gap-2">
          <select className="input max-w-xs" disabled={!editar} value={it.pessoa_id} onChange={(e) => setItens((s) => s.map((x, j) => (j === i ? { ...x, pessoa_id: e.target.value } : x)))}>
            <option value="">Selecione…</option>{(pessoas.data ?? []).map((p) => <option key={p.id} value={p.id}>{p.nome_razao}</option>)}</select>
          <input className="input w-24" disabled={!editar} value={it.percentual} onChange={(e) => setItens((s) => s.map((x, j) => (j === i ? { ...x, percentual: e.target.value } : x)))} /> %
          <label className="flex items-center gap-1 text-sm"><input type="checkbox" disabled={!editar} checked={it.principal} onChange={(e) => setItens((s) => s.map((x, j) => (j === i ? { ...x, principal: e.target.checked } : x)))} />Principal</label>
          {editar && <button className="btn-ghost" onClick={() => setItens((s) => s.filter((_, j) => j !== i))} aria-label="Remover"><Trash2 size={14} /></button>}
        </div>))}
      {editar && <div className="mt-2 flex items-center gap-3"><button className="btn-ghost" onClick={() => setItens((s) => [...s, { pessoa_id: '', percentual: '', principal: false }])}><Plus size={14} />Adicionar</button>
        <button className="btn-primary" onClick={() => m.mutate()} disabled={m.isPending}>Salvar proprietários</button><span className={soma === 100 || !itens.length ? 'text-sm text-green-600' : 'text-sm text-amber-600'}>Soma: {soma.toFixed(2)}%</span></div>}
    </section>
  );
}

function Caracteristicas({ imovel, editar, onSalvo }: { imovel: ImovelDetalhe; editar: boolean; onSalvo: () => void }) {
  const [itens, setItens] = useState(imovel.caracteristicas.map((c) => ({ chave: c.chave, valor: c.valor ?? '', publico: c.publico })));
  const m = useMutation({ mutationFn: () => api.put(`/imoveis/${imovel.id}/caracteristicas`, itens.filter((i) => i.chave.trim()).map((i) => ({ ...i, valor: i.valor || null }))), onSuccess: onSalvo });
  return (
    <section className="card"><h2 className="mb-3 font-semibold">Características</h2><Erro msg={m.error ? msgErro(m.error) : null} />
      {itens.map((it, i) => (
        <div key={i} className="mb-2 flex flex-wrap items-center gap-2">
          <input className="input max-w-[220px]" placeholder="Ex.: Piscina" disabled={!editar} value={it.chave} onChange={(e) => setItens((s) => s.map((x, j) => (j === i ? { ...x, chave: e.target.value } : x)))} />
          <input className="input max-w-[220px]" placeholder="Valor (opcional)" disabled={!editar} value={it.valor} onChange={(e) => setItens((s) => s.map((x, j) => (j === i ? { ...x, valor: e.target.value } : x)))} />
          <label className="flex items-center gap-1 text-sm"><input type="checkbox" disabled={!editar} checked={it.publico} onChange={(e) => setItens((s) => s.map((x, j) => (j === i ? { ...x, publico: e.target.checked } : x)))} />Público</label>
          {editar && <button className="btn-ghost" onClick={() => setItens((s) => s.filter((_, j) => j !== i))} aria-label="Remover"><Trash2 size={14} /></button>}
        </div>))}
      {editar && <div className="mt-2 flex gap-2"><button className="btn-ghost" onClick={() => setItens((s) => [...s, { chave: '', valor: '', publico: true }])}><Plus size={14} />Adicionar</button><button className="btn-primary" onClick={() => m.mutate()} disabled={m.isPending}>Salvar características</button></div>}
    </section>
  );
}

function Midias({ imovel, editar, onSalvo }: { imovel: ImovelDetalhe; editar: boolean; onSalvo: () => void }) {
  const up = useMutation({ mutationFn: async (f: File) => { const fd = new FormData(); fd.append('arquivo', f); fd.append('tipo', f.type.startsWith('video') ? 'VIDEO' : f.type === 'application/pdf' ? 'DOCUMENTO' : 'FOTO'); return api.post(`/imoveis/${imovel.id}/midias`, fd); }, onSuccess: onSalvo });
  const del = useMutation({ mutationFn: (id: string) => api.del(`/midias/${id}`), onSuccess: onSalvo });
  const capa = useMutation({ mutationFn: (id: string) => api.patch(`/midias/${id}`, { capa: true }), onSuccess: onSalvo });
  return (
    <section className="card"><h2 className="mb-3 font-semibold">Fotos e documentos</h2><Erro msg={up.error ? msgErro(up.error) : null} />
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">{imovel.midias.map((m) => (
        <div key={m.id} className="rounded-md border p-2 text-xs">
          {m.url && m.tipo === 'FOTO' ? /* eslint-disable-next-line @next/next/no-img-element */ <img src={m.url} alt={m.legenda ?? ''} className="h-28 w-full rounded object-cover" /> : <a className="block h-28 text-brand-600 underline" href={m.url ?? '#'} target="_blank" rel="noreferrer">{m.tipo}</a>}
          <p className="mt-1 truncate">{m.legenda ?? ''}{m.capa && ' · capa'}</p>
          {editar && <div className="mt-1 flex gap-1"><button className="btn-ghost px-2 py-1" onClick={() => capa.mutate(m.id)}>Capa</button><button className="btn-ghost px-2 py-1" onClick={() => confirm('Excluir arquivo?') && del.mutate(m.id)}>Excluir</button></div>}
        </div>))}</div>
      {!imovel.midias.length && <p className="text-sm text-slate-500">Nenhum arquivo enviado.</p>}
      {editar && <label className="btn-ghost mt-3 cursor-pointer"><Upload size={14} />Enviar arquivo<input type="file" hidden onChange={(e) => { const f = e.target.files?.[0]; if (f) up.mutate(f); e.target.value = ''; }} /></label>}
    </section>
  );
}
