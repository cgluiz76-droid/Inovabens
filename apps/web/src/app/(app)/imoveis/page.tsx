'use client';
import Link from 'next/link';
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Plus } from 'lucide-react';
import { SITUACAO_IMOVEL } from '@inovabens/shared';
import { api, qs } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { brl, rotulo } from '@/lib/fmt';
import { Badge, Carregando, Paginacao, Select, Titulo, Vazio } from '@/components/ui';

interface Item { id: string; codigo: string; titulo: string; tipo: string; situacao: string; finalidade: string; cidade: string | null; bairro: string | null; uf: string | null; preco_venda: string | null; preco_locacao: string | null; corretor: { nome: string } | null }
export default function Imoveis() {
  const { pode } = useAuth(); const [q, setQ] = useState(''); const [situacao, setSituacao] = useState(''); const [page, setPage] = useState(1);
  const { data, isLoading } = useQuery({ queryKey: ['imoveis', q, situacao, page], queryFn: () => api.get<{ data: Item[]; total: number; pageSize: number }>(`/imoveis${qs({ q, situacao, page })}`) });
  return (
    <>
      <Titulo acoes={pode('imoveis:criar') && <Link href="/imoveis/novo" className="btn-primary"><Plus size={16} />Novo imóvel</Link>}>Imóveis</Titulo>
      <div className="mb-3 flex flex-wrap gap-2">
        <input className="input max-w-xs" placeholder="Buscar por código, título, endereço…" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} />
        <div className="w-52"><Select value={situacao} onChange={(e) => { setSituacao(e.target.value); setPage(1); }} opcoes={[{ v: '', t: 'Todas as situações' }, ...SITUACAO_IMOVEL.map((s) => ({ v: s, t: rotulo(s) }))]} /></div>
      </div>
      {isLoading ? <Carregando /> : !data?.data.length ? <Vazio>Nenhum imóvel encontrado.</Vazio> : (
        <div className="overflow-x-auto rounded-lg border bg-white"><table className="w-full">
          <thead className="border-b bg-slate-50"><tr>{['Código', 'Imóvel', 'Local', 'Situação', 'Preço', 'Corretor'].map((h) => <th key={h} className="th">{h}</th>)}</tr></thead>
          <tbody className="divide-y">{data.data.map((i) => (
            <tr key={i.id} className="hover:bg-slate-50">
              <td className="td font-mono text-xs"><Link className="text-brand-600 hover:underline" href={`/imoveis/${i.id}`}>{i.codigo}</Link></td>
              <td className="td"><Link href={`/imoveis/${i.id}`} className="font-medium">{i.titulo}</Link><p className="text-xs text-slate-500">{rotulo(i.tipo)} · {rotulo(i.finalidade)}</p></td>
              <td className="td">{[i.bairro, i.cidade && `${i.cidade}${i.uf ? '/' + i.uf : ''}`].filter(Boolean).join(' · ') || '—'}</td>
              <td className="td"><Badge>{rotulo(i.situacao)}</Badge></td>
              <td className="td">{brl(i.preco_venda ?? i.preco_locacao)}</td><td className="td">{i.corretor?.nome ?? '—'}</td>
            </tr>))}</tbody></table></div>)}
      {data && <Paginacao page={page} pageSize={data.pageSize} total={data.total} onChange={setPage} />}
    </>
  );
}
