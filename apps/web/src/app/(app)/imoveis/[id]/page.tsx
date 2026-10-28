'use client';
import { use, useState } from 'react';
import { useSearchParams } from 'next/navigation';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { ArrowLeft, Download } from 'lucide-react';
import clsx from 'clsx';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { rotulo } from '@/lib/fmt';
import AgendaImovel from '@/components/imovel/agenda-imovel';
import Ficha, { type ImovelDetalhe } from '@/components/imovel/ficha';
import Financeiro from '@/components/imovel/financeiro';
import Memorandos from '@/components/imovel/memorandos';
import Regularizacao from '@/components/imovel/regularizacao';
import { Badge, Carregando, Erro } from '@/components/ui';

export default function ImovelPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params); const { pode } = useAuth(); const sp = useSearchParams();
  const abas = [['ficha', 'Ficha'], ...(pode('financeiro:ver') ? [['financeiro', 'Financeiro']] : []), ['regularizacao', 'Regularização'], ['memorandos', 'Memorandos'], ['agenda', 'Agenda']];
  const [aba, setAba] = useState(abas.some((a) => a[0] === sp.get('aba')) ? sp.get('aba')! : 'ficha');
  const [erro, setErro] = useState<string | null>(null);
  const q = useQuery({ queryKey: ['imovel', id], queryFn: () => api.get<ImovelDetalhe>(`/imoveis/${id}`) });
  if (q.isLoading) return <Carregando />;
  if (q.error || !q.data) return <><Link href="/imoveis" className="btn-ghost mb-3"><ArrowLeft size={14} />Voltar</Link><Erro msg={msgErro(q.error)} /></>;
  const i = q.data;
  const baixar = async (ext: string) => { setErro(null); try { await api.baixar(`/imoveis/${id}/ficha.${ext}`, `ficha-${i.codigo}.${ext}`); } catch (e) { setErro(msgErro(e)); } };
  return (
    <>
      <Link href="/imoveis" className="mb-2 inline-flex items-center gap-1 text-sm text-slate-500 hover:underline"><ArrowLeft size={14} />Imóveis</Link>
      <div className="mb-4 flex flex-wrap items-start justify-between gap-2">
        <div><p className="font-mono text-xs text-slate-500">{i.codigo}</p><h1 className="text-xl font-semibold">{i.titulo}</h1><Badge>{rotulo(String(i.situacao))}</Badge></div>
        {pode('imoveis:exportar') && <div className="flex gap-2"><button className="btn-ghost" onClick={() => baixar('pdf')}><Download size={14} />Ficha PDF</button><button className="btn-ghost" onClick={() => baixar('xlsx')}><Download size={14} />Excel</button></div>}
      </div>
      <Erro msg={erro} />
      <div className="mb-4 flex gap-1 overflow-x-auto border-b">{abas.map(([k, t]) => <button key={k} onClick={() => setAba(k)} className={clsx('whitespace-nowrap border-b-2 px-4 py-2 text-sm', aba === k ? 'border-brand-600 font-semibold text-brand-700' : 'border-transparent text-slate-500 hover:text-slate-800')}>{t}</button>)}</div>
      {aba === 'ficha' && <Ficha imovel={i} />}
      {aba === 'financeiro' && <Financeiro imovelId={id} />}
      {aba === 'regularizacao' && <Regularizacao imovelId={id} />}
      {aba === 'memorandos' && <Memorandos imovelId={id} />}
      {aba === 'agenda' && <AgendaImovel imovelId={id} />}
    </>
  );
}
