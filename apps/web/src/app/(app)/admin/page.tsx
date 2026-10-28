'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import clsx from 'clsx';
import { CODIGO_INDICE, PERMISSOES } from '@inovabens/shared';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { dataHora } from '@/lib/fmt';
import { Badge, Campo, Carregando, Erro, Modal, Select, Titulo } from '@/components/ui';

interface Usr { id: string; nome: string; email: string; ativo: boolean; perfil: { id: string; nome: string } }
interface Perfil { id: string; nome: string; descricao: string | null; escopo_proprio: boolean; permissoes: string[] }

export default function Admin() {
  const { pode } = useAuth();
  const abas = [pode('usuarios:gerir') && ['usuarios', 'Usuários'], pode('rbac:gerir') && ['perfis', 'Perfis e permissões'], pode('admin:config') && ['config', 'Configurações'], pode('auditoria:ver') && ['auditoria', 'Auditoria']].filter(Boolean) as string[][];
  const [aba, setAba] = useState(abas[0]?.[0] ?? '');
  return (
    <>
      <Titulo>Administração</Titulo>
      <div className="mb-4 flex gap-1 overflow-x-auto border-b">{abas.map(([k, t]) => <button key={k} onClick={() => setAba(k)} className={clsx('whitespace-nowrap border-b-2 px-4 py-2 text-sm', aba === k ? 'border-brand-600 font-semibold text-brand-700' : 'border-transparent text-slate-500')}>{t}</button>)}</div>
      {aba === 'usuarios' && <Usuarios />}{aba === 'perfis' && <Perfis />}{aba === 'config' && <Config />}{aba === 'auditoria' && <Auditoria />}
    </>
  );
}

function Usuarios() {
  const qc = useQueryClient(); const [edit, setEdit] = useState<Partial<Usr> | null>(null);
  const q = useQuery({ queryKey: ['users'], queryFn: () => api.get<{ data: Usr[] }>('/users?pageSize=100') });
  const status = useMutation({ mutationFn: (u: Usr) => api.patch(`/users/${u.id}/status`, { ativo: !u.ativo }), onSuccess: () => qc.invalidateQueries({ queryKey: ['users'] }) });
  return (
    <div><div className="mb-3 flex justify-end"><button className="btn-primary" onClick={() => setEdit({})}>Novo usuário</button></div><Erro msg={status.error ? msgErro(status.error) : null} />
      {q.isLoading ? <Carregando /> : <div className="overflow-x-auto rounded-lg border bg-white"><table className="w-full"><thead className="border-b bg-slate-50"><tr>{['Nome', 'E-mail', 'Perfil', 'Situação', ''].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
        <tbody className="divide-y">{q.data?.data.map((u) => <tr key={u.id}><td className="td font-medium">{u.nome}</td><td className="td">{u.email}</td><td className="td"><Badge>{u.perfil.nome}</Badge></td><td className="td">{u.ativo ? 'Ativo' : <span className="text-red-600">Inativo</span>}</td>
          <td className="td text-right"><button className="btn-ghost" onClick={() => setEdit(u)}>Editar</button> <button className="btn-ghost" onClick={() => status.mutate(u)}>{u.ativo ? 'Desativar' : 'Ativar'}</button></td></tr>)}</tbody></table></div>}
      {edit && <FormUsuario u={edit} onClose={() => setEdit(null)} onOk={() => { setEdit(null); qc.invalidateQueries({ queryKey: ['users'] }); }} />}
    </div>
  );
}
function FormUsuario({ u, onClose, onOk }: { u: Partial<Usr>; onClose: () => void; onOk: () => void }) {
  const perfis = useQuery({ queryKey: ['roles'], queryFn: () => api.get<Perfil[]>('/roles') });
  const [f, setF] = useState({ nome: u.nome ?? '', email: u.email ?? '', senha: '', perfil_id: u.perfil?.id ?? '' });
  const m = useMutation({ mutationFn: () => { const corpo = { nome: f.nome, email: f.email, perfil_id: f.perfil_id || perfis.data?.[0]?.id, ...(f.senha ? { senha: f.senha } : {}) }; return u.id ? api.patch(`/users/${u.id}`, corpo) : api.post('/users', corpo); }, onSuccess: onOk });
  const set = (k: string, v: string) => setF((s) => ({ ...s, [k]: v }));
  return <Modal titulo={u.id ? 'Editar usuário' : 'Novo usuário'} onClose={onClose}><form className="space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}><Erro msg={m.error ? msgErro(m.error) : null} />
    <Campo label="Nome"><input className="input" required value={f.nome} onChange={(e) => set('nome', e.target.value)} /></Campo>
    <Campo label="E-mail"><input className="input" type="email" required value={f.email} onChange={(e) => set('email', e.target.value)} /></Campo>
    <Campo label={u.id ? 'Nova senha (deixe em branco para manter)' : 'Senha (mín. 10 caracteres, letras e números)'}><input className="input" type="password" autoComplete="new-password" required={!u.id} value={f.senha} onChange={(e) => set('senha', e.target.value)} /></Campo>
    <Campo label="Perfil"><Select value={f.perfil_id || perfis.data?.[0]?.id || ''} onChange={(e) => set('perfil_id', e.target.value)} opcoes={(perfis.data ?? []).map((p) => ({ v: p.id, t: p.nome }))} /></Campo>
    <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-primary" disabled={m.isPending}>Salvar</button></div></form></Modal>;
}

function Perfis() {
  const qc = useQueryClient(); const q = useQuery({ queryKey: ['roles'], queryFn: () => api.get<Perfil[]>('/roles') });
  const [edit, setEdit] = useState<Partial<Perfil> | null>(null);
  const del = useMutation({ mutationFn: (id: string) => api.del(`/roles/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: ['roles'] }) });
  return (
    <div><div className="mb-3 flex justify-end"><button className="btn-primary" onClick={() => setEdit({ permissoes: [], escopo_proprio: false })}>Novo perfil</button></div><Erro msg={del.error ? msgErro(del.error) : null} />
      {q.isLoading ? <Carregando /> : <div className="grid gap-3 md:grid-cols-2">{q.data?.map((p) => <div key={p.id} className="card"><div className="flex justify-between"><b>{p.nome}</b>{p.escopo_proprio && <Badge>vê só o que é seu</Badge>}</div><p className="text-xs text-slate-500">{p.descricao}</p><p className="mt-2 text-xs text-slate-600">{p.permissoes.length} permissões</p>
        {p.nome !== 'Administrador' && <div className="mt-2 flex gap-2"><button className="btn-ghost" onClick={() => setEdit(p)}>Editar</button><button className="btn-ghost" onClick={() => confirm(`Excluir o perfil ${p.nome}?`) && del.mutate(p.id)}>Excluir</button></div>}</div>)}</div>}
      {edit && <FormPerfil p={edit} onClose={() => setEdit(null)} onOk={() => { setEdit(null); qc.invalidateQueries({ queryKey: ['roles'] }); }} />}
    </div>
  );
}
function FormPerfil({ p, onClose, onOk }: { p: Partial<Perfil>; onClose: () => void; onOk: () => void }) {
  const [f, setF] = useState({ nome: p.nome ?? '', descricao: p.descricao ?? '', escopo_proprio: !!p.escopo_proprio, permissoes: new Set(p.permissoes ?? []) });
  const m = useMutation({ mutationFn: () => { const corpo = { nome: f.nome, descricao: f.descricao || null, escopo_proprio: f.escopo_proprio, permissoes: [...f.permissoes] }; return p.id ? api.patch(`/roles/${p.id}`, corpo) : api.post('/roles', corpo); }, onSuccess: onOk });
  const grupos = [...new Set(PERMISSOES.map((x) => x.split(':')[0]))];
  const alternar = (x: string) => setF((s) => { const n = new Set(s.permissoes); n.has(x) ? n.delete(x) : n.add(x); return { ...s, permissoes: n }; });
  return <Modal titulo={p.id ? 'Editar perfil' : 'Novo perfil'} onClose={onClose} largo><form className="space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}><Erro msg={m.error ? msgErro(m.error) : null} />
    <div className="grid gap-3 md:grid-cols-2"><Campo label="Nome"><input className="input" required value={f.nome} onChange={(e) => setF({ ...f, nome: e.target.value })} /></Campo><Campo label="Descrição"><input className="input" value={f.descricao} onChange={(e) => setF({ ...f, descricao: e.target.value })} /></Campo></div>
    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={f.escopo_proprio} onChange={(e) => setF({ ...f, escopo_proprio: e.target.checked })} />Enxerga apenas imóveis, leads e compromissos pelos quais é responsável</label>
    <div className="grid gap-3 md:grid-cols-3">{grupos.map((g) => <fieldset key={g} className="rounded border p-2"><legend className="px-1 text-xs font-semibold uppercase text-slate-500">{g}</legend>
      {PERMISSOES.filter((x) => x.startsWith(g + ':')).map((x) => <label key={x} className="flex items-center gap-1.5 text-sm"><input type="checkbox" checked={f.permissoes.has(x)} onChange={() => alternar(x)} />{x.split(':')[1]}</label>)}</fieldset>)}</div>
    <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-primary" disabled={m.isPending}>Salvar</button></div></form></Modal>;
}

function Config() {
  const qc = useQueryClient();
  const cats = useQuery({ queryKey: ['categorias-despesa-adm'], queryFn: () => api.get<{ id: string; nome: string; ativo: boolean }[]>('/categorias-despesa') });
  const estagios = useQuery({ queryKey: ['estagios-adm'], queryFn: () => api.get<{ id: string; funil: string; nome: string; ordem: number; cor: string; tipo: string }[]>('/crm/estagios') });
  const [nova, setNova] = useState(''); const [nomeEst, setNomeEst] = useState(''); const [funil, setFunil] = useState('VENDA');
  const addCat = useMutation({ mutationFn: () => api.post('/categorias-despesa', { nome: nova }), onSuccess: () => { setNova(''); qc.invalidateQueries({ queryKey: ['categorias-despesa-adm'] }); } });
  const addEst = useMutation({ mutationFn: () => api.post('/crm/estagios', { funil, nome: nomeEst, ordem: (estagios.data?.filter((e) => e.funil === funil).length ?? 0) }), onSuccess: () => { setNomeEst(''); qc.invalidateQueries({ queryKey: ['estagios-adm'] }); qc.invalidateQueries({ queryKey: ['pipeline'] }); } });
  const delEst = useMutation({ mutationFn: (id: string) => api.del(`/crm/estagios/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: ['estagios-adm'] }) });
  const renomear = useMutation({ mutationFn: (e: { id: string; nome: string }) => api.patch(`/crm/estagios/${e.id}`, { nome: e.nome }), onSuccess: () => qc.invalidateQueries({ queryKey: ['estagios-adm'] }) });
  const sync = useMutation({ mutationFn: () => api.post<unknown>('/indices/sync') });
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <section className="card"><h2 className="mb-2 font-semibold">Categorias de despesa</h2><Erro msg={addCat.error ? msgErro(addCat.error) : null} />
        <ul className="mb-3 flex flex-wrap gap-1">{cats.data?.map((c) => <li key={c.id}><Badge>{c.nome}</Badge></li>)}</ul>
        <div className="flex gap-2"><input className="input" placeholder="Nova categoria" value={nova} onChange={(e) => setNova(e.target.value)} /><button className="btn-primary" disabled={nova.length < 2} onClick={() => addCat.mutate()}>Adicionar</button></div></section>
      <section className="card"><h2 className="mb-2 font-semibold">Índices econômicos</h2><p className="mb-2 text-sm text-slate-500">Séries ({CODIGO_INDICE.join(', ')}) usadas na correção do custo. Atualizadas automaticamente todo mês pelo Banco Central.</p>
        <Erro msg={sync.error ? msgErro(sync.error) : null} />{sync.isSuccess && <p className="mb-2 text-sm text-green-700">Sincronização concluída.</p>}
        <button className="btn-ghost" onClick={() => sync.mutate()} disabled={sync.isPending}>{sync.isPending ? 'Buscando…' : 'Sincronizar com o Banco Central agora'}</button></section>
      <section className="card lg:col-span-2"><h2 className="mb-2 font-semibold">Estágios do funil (CRM)</h2><Erro msg={(delEst.error ?? addEst.error ?? renomear.error) ? msgErro(delEst.error ?? addEst.error ?? renomear.error) : null} />
        <div className="grid gap-4 md:grid-cols-2">{['VENDA', 'LOCACAO'].map((fu) => <div key={fu}><h3 className="mb-1 text-sm font-semibold">{fu === 'VENDA' ? 'Venda' : 'Locação'}</h3>
          {estagios.data?.filter((e) => e.funil === fu).sort((a, b) => a.ordem - b.ordem).map((e) => <div key={e.id} className="mb-1 flex items-center gap-2"><span className="h-3 w-3 rounded-full" style={{ background: e.cor }} />
            <input className="input" defaultValue={e.nome} onBlur={(ev) => ev.target.value !== e.nome && ev.target.value.length > 1 && renomear.mutate({ id: e.id, nome: ev.target.value })} />
            <button className="btn-ghost" onClick={() => confirm(`Excluir o estágio ${e.nome}?`) && delEst.mutate(e.id)}>×</button></div>)}</div>)}</div>
        <div className="mt-3 flex gap-2"><div className="w-36"><Select value={funil} onChange={(e) => setFunil(e.target.value)} opcoes={[{ v: 'VENDA', t: 'Venda' }, { v: 'LOCACAO', t: 'Locação' }]} /></div><input className="input max-w-xs" placeholder="Novo estágio" value={nomeEst} onChange={(e) => setNomeEst(e.target.value)} /><button className="btn-primary" disabled={nomeEst.length < 2} onClick={() => addEst.mutate()}>Adicionar estágio</button></div></section>
    </div>
  );
}

function Auditoria() {
  const [page, setPage] = useState(1);
  const q = useQuery({ queryKey: ['auditoria', page], queryFn: () => api.get<{ data: { id: string; criado_em: string; entidade: string; acao: string; usuario: { nome: string } | null }[]; total: number; pageSize: number }>(`/auditoria?page=${page}`) });
  if (q.isLoading || !q.data) return <Carregando />;
  return (
    <div><div className="overflow-x-auto rounded-lg border bg-white"><table className="w-full"><thead className="border-b bg-slate-50"><tr>{['Quando', 'Usuário', 'Entidade', 'Ação'].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
      <tbody className="divide-y">{q.data.data.map((a) => <tr key={a.id}><td className="td">{dataHora(a.criado_em)}</td><td className="td">{a.usuario?.nome ?? '—'}</td><td className="td">{a.entidade}</td><td className="td"><Badge>{a.acao}</Badge></td></tr>)}</tbody></table></div>
      <div className="mt-3 flex items-center justify-end gap-2 text-sm"><button className="btn-ghost" disabled={page <= 1} onClick={() => setPage(page - 1)}>Anterior</button>{page}<button className="btn-ghost" disabled={page * q.data.pageSize >= q.data.total} onClick={() => setPage(page + 1)}>Próxima</button></div></div>
  );
}
