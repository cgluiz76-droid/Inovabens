'use client';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { dataHora, rotulo } from '@/lib/fmt';
import { Badge, Carregando, Titulo, Vazio } from '@/components/ui';

interface Dash {
  imoveis_por_situacao: { situacao: string; total: number }[]; leads_por_estagio: { estagio: string; total: number; cor: string }[];
  alertas_abertos: number; lancamentos_nao_pagos: number | null; proximos_compromissos: { id: string; titulo: string; inicio_em: string; imovel?: { id: string; codigo: string } }[];
}
const Kpi = ({ t, v, href }: { t: string; v: number | string; href?: string }) => {
  const c = <div className="card"><p className="text-xs uppercase text-slate-500">{t}</p><p className="mt-1 text-2xl font-semibold">{v}</p></div>;
  return href ? <Link href={href}>{c}</Link> : c;
};
export default function Dashboard() {
  const { data, isLoading } = useQuery({ queryKey: ['dashboard'], queryFn: () => api.get<Dash>('/dashboard') });
  if (isLoading || !data) return <Carregando />;
  const totalImoveis = data.imoveis_por_situacao.reduce((s, x) => s + x.total, 0);
  const maxLead = Math.max(1, ...data.leads_por_estagio.map((l) => l.total));
  return (
    <>
      <Titulo>Painel</Titulo>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Kpi t="Imóveis" v={totalImoveis} href="/imoveis" />
        <Kpi t="Alertas abertos" v={data.alertas_abertos} href="/alertas" />
        {data.lancamentos_nao_pagos !== null && data.lancamentos_nao_pagos !== undefined && <Kpi t="Despesas não pagas" v={data.lancamentos_nao_pagos} />}
        <Kpi t="Leads no funil" v={data.leads_por_estagio.reduce((s, x) => s + x.total, 0)} href="/crm" />
      </div>
      <div className="mt-4 grid gap-4 lg:grid-cols-3">
        <section className="card"><h2 className="mb-2 font-semibold">Imóveis por situação</h2>
          {data.imoveis_por_situacao.length ? data.imoveis_por_situacao.map((s) => <div key={s.situacao} className="flex justify-between border-b py-1.5 text-sm last:border-0"><span>{rotulo(s.situacao)}</span><b>{s.total}</b></div>) : <Vazio>Sem imóveis.</Vazio>}</section>
        <section className="card"><h2 className="mb-2 font-semibold">Funil de vendas</h2>
          {data.leads_por_estagio.map((l) => <div key={l.estagio} className="mb-2 text-sm"><div className="flex justify-between"><span>{l.estagio}</span><b>{l.total}</b></div><div className="h-2 rounded bg-slate-100"><div className="h-2 rounded" style={{ width: `${(l.total / maxLead) * 100}%`, background: l.cor }} /></div></div>)}</section>
        <section className="card"><h2 className="mb-2 font-semibold">Próximos compromissos</h2>
          {data.proximos_compromissos.length ? data.proximos_compromissos.map((c) => <div key={c.id} className="border-b py-1.5 text-sm last:border-0"><p className="font-medium">{c.titulo}</p><p className="text-xs text-slate-500">{dataHora(c.inicio_em)} {c.imovel && <Badge>{c.imovel.codigo}</Badge>}</p></div>) : <Vazio>Nada agendado.</Vazio>}</section>
      </div>
    </>
  );
}
