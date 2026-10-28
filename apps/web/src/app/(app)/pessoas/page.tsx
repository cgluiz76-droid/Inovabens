'use client';
import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { PAPEL_PESSOA } from '@inovabens/shared';
import { api, ApiError, msgErro, qs } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { rotulo } from '@/lib/fmt';
import { Badge, Campo, Carregando, Erro, Modal, Paginacao, Select, Titulo, Vazio } from '@/components/ui';

interface Pessoa { id: string; tipo: string; nome_razao: string; cpf_cnpj: string | null; email: string | null; telefone: string | null; cidade: string | null; papeis: { papel: string; especialidade: string | null }[] }
export default function Pessoas() {
  const { pode } = useAuth(); const qc = useQueryClient();
  const [q, setQ] = useState(''); const [papel, setPapel] = useState(''); const [page, setPage] = useState(1); const [edit, setEdit] = useState<Partial<Pessoa> | null>(null);
  const { data, isLoading } = useQuery({ queryKey: ['pessoas', q, papel, page], queryFn: () => api.get<{ data: Pessoa[]; total: number; pageSize: number }>(`/pessoas${qs({ q, papel, page })}`) });
  const excluir = useMutation({ mutationFn: (id: string) => api.del(`/pessoas/${id}`), onSuccess: () => qc.invalidateQueries({ queryKey: ['pessoas'] }) });
  return (
    <>
      <Titulo acoes={pode('pessoas:criar') && <button className="btn-primary" onClick={() => setEdit({ tipo: 'PF', papeis: [] })}><Plus size={16} />Nova pessoa</button>}>Pessoas</Titulo>
      <p className="mb-3 text-sm text-slate-500">Clientes, proprietários, fiadores, parceiros, prestadores e terceiros.</p>
      <div className="mb-3 flex flex-wrap gap-2">
        <input className="input max-w-xs" placeholder="Nome, CPF/CNPJ, e-mail…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} />
        <div className="w-48"><Select value={papel} onChange={(e) => { setPapel(e.target.value); setPage(1); }} opcoes={[{ v: '', t: 'Todos os papéis' }, ...PAPEL_PESSOA.map((p) => ({ v: p, t: rotulo(p) }))]} /></div>
      </div>
      {isLoading ? <Carregando /> : !data?.data.length ? <Vazio>Nenhuma pessoa encontrada.</Vazio> : (
        <div className="overflow-x-auto rounded-lg border bg-white"><table className="w-full">
          <thead className="border-b bg-slate-50"><tr>{['Nome', 'Documento', 'Contato', 'Papéis', ''].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
          <tbody className="divide-y">{data.data.map((p) => (
            <tr key={p.id}><td className="td font-medium">{p.nome_razao}</td><td className="td">{p.cpf_cnpj ?? '—'}</td>
              <td className="td">{p.telefone ?? ''}<p className="text-xs text-slate-500">{p.email}</p></td>
              <td className="td space-x-1">{p.papeis.map((x) => <Badge key={x.papel}>{rotulo(x.papel)}{x.especialidade ? ` · ${x.especialidade}` : ''}</Badge>)}</td>
              <td className="td text-right">{pode('pessoas:editar') && <button className="btn-ghost" onClick={() => setEdit(p)}>Editar</button>}{' '}
                {pode('pessoas:excluir') && <button className="btn-ghost" onClick={() => confirm(`Excluir ${p.nome_razao}?`) && excluir.mutate(p.id)}>Excluir</button>}</td></tr>))}</tbody></table></div>)}
      {data && <Paginacao page={page} pageSize={data.pageSize} total={data.total} onChange={setPage} />}
      {edit && <Form inicial={edit} onClose={() => setEdit(null)} onOk={() => { setEdit(null); qc.invalidateQueries({ queryKey: ['pessoas'] }); }} />}
    </>
  );
}
function Form({ inicial, onClose, onOk }: { inicial: Partial<Pessoa>; onClose: () => void; onOk: () => void }) {
  const [v, setV] = useState({ tipo: inicial.tipo ?? 'PF', nome_razao: inicial.nome_razao ?? '', cpf_cnpj: inicial.cpf_cnpj ?? '', email: inicial.email ?? '', telefone: inicial.telefone ?? '', cidade: inicial.cidade ?? '' });
  const [papeis, setPapeis] = useState<{ papel: string; especialidade: string }[]>((inicial.papeis ?? []).map((p) => ({ papel: p.papel, especialidade: p.especialidade ?? '' })));
  const [campos, setCampos] = useState<Record<string, string[]>>({});
  const m = useMutation({
    mutationFn: () => { const corpo = { ...Object.fromEntries(Object.entries(v).map(([k, x]) => [k, x === '' ? null : x])), nome_razao: v.nome_razao, papeis: papeis.map((p) => ({ papel: p.papel, ...(p.especialidade ? { especialidade: p.especialidade } : {}) })) }; return inicial.id ? api.patch(`/pessoas/${inicial.id}`, corpo) : api.post('/pessoas', corpo); },
    onSuccess: onOk, onError: (e) => { if (e instanceof ApiError) setCampos(e.campos); },
  });
  const set = (k: string, x: string) => setV((s) => ({ ...s, [k]: x }));
  const tem = (p: string) => papeis.some((x) => x.papel === p);
  return (
    <Modal titulo={inicial.id ? 'Editar pessoa' : 'Nova pessoa'} onClose={onClose} largo>
      <form className="space-y-3" onSubmit={(e) => { e.preventDefault(); m.mutate(); }}>
        <Erro msg={m.error ? msgErro(m.error) : null} />
        <div className="grid gap-3 md:grid-cols-3">
          <Campo label="Tipo"><Select value={v.tipo} onChange={(e) => set('tipo', e.target.value)} opcoes={[{ v: 'PF', t: 'Pessoa física' }, { v: 'PJ', t: 'Pessoa jurídica' }]} /></Campo>
          <Campo label="Nome / Razão social *" className="md:col-span-2" erro={campos.nome_razao?.[0]}><input className="input" required value={v.nome_razao} onChange={(e) => set('nome_razao', e.target.value)} /></Campo>
          <Campo label="CPF / CNPJ" erro={campos.cpf_cnpj?.[0]}><input className="input" value={v.cpf_cnpj} onChange={(e) => set('cpf_cnpj', e.target.value)} /></Campo>
          <Campo label="E-mail" erro={campos.email?.[0]}><input className="input" type="email" value={v.email} onChange={(e) => set('email', e.target.value)} /></Campo>
          <Campo label="Telefone"><input className="input" value={v.telefone} onChange={(e) => set('telefone', e.target.value)} /></Campo>
        </div>
        <fieldset><legend className="mb-1 text-sm font-medium text-slate-600">Papéis</legend>
          <div className="flex flex-wrap gap-3 text-sm">{PAPEL_PESSOA.map((p) => <label key={p} className="flex items-center gap-1.5"><input type="checkbox" checked={tem(p)} onChange={(e) => setPapeis((s) => e.target.checked ? [...s, { papel: p, especialidade: '' }] : s.filter((x) => x.papel !== p))} />{rotulo(p)}</label>)}</div>
          {tem('PRESTADOR') && <Campo label="Especialidade do prestador" className="mt-2"><input className="input" placeholder="Advogado, topógrafo, engenheiro…" value={papeis.find((x) => x.papel === 'PRESTADOR')!.especialidade} onChange={(e) => setPapeis((s) => s.map((x) => x.papel === 'PRESTADOR' ? { ...x, especialidade: e.target.value } : x))} /></Campo>}
        </fieldset>
        <div className="flex justify-end gap-2"><button type="button" className="btn-ghost" onClick={onClose}>Cancelar</button><button className="btn-primary" disabled={m.isPending}>Salvar</button></div>
      </form>
    </Modal>
  );
}
