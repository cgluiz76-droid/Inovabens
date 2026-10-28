'use client';
import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Bell, Building2, CalendarDays, KanbanSquare, LayoutDashboard, LogOut, Settings, Users, type LucideIcon } from 'lucide-react';
import clsx from 'clsx';
import { useState, type ReactNode } from 'react';
import { api } from '@/lib/api';
import { useExigirLogin } from '@/lib/auth';

const itens: { href: string; t: string; i: LucideIcon; perm: string }[] = [
  { href: '/dashboard', t: 'Painel', i: LayoutDashboard, perm: '' },
  { href: '/imoveis', t: 'Imóveis', i: Building2, perm: 'imoveis:ver' },
  { href: '/pessoas', t: 'Pessoas', i: Users, perm: 'pessoas:ver' },
  { href: '/crm', t: 'CRM', i: KanbanSquare, perm: 'crm:ver' },
  { href: '/agenda', t: 'Agenda', i: CalendarDays, perm: 'agenda:ver' },
  { href: '/admin', t: 'Administração', i: Settings, perm: 'usuarios:gerir' },
];
export default function AppLayout({ children }: { children: ReactNode }) {
  const { usuario, carregando, pode, sair } = useExigirLogin();
  const path = usePathname(); const router = useRouter(); const [menu, setMenu] = useState(false);
  const alertas = useQuery({ queryKey: ['alertas-contagem'], queryFn: () => api.get<{ abertos: number }>('/alertas/contagem'), enabled: !!usuario, refetchInterval: 60_000 });
  if (carregando || !usuario) return <p className="p-8 text-center text-slate-400">Carregando…</p>;
  const nav = itens.filter((i) => !i.perm || pode(i.perm) || (i.href === '/admin' && (pode('rbac:gerir') || pode('admin:config'))));
  return (
    <div className="flex min-h-screen">
      <aside className={clsx('fixed inset-y-0 left-0 z-40 w-56 bg-brand-900 text-slate-100 transition-transform md:static md:translate-x-0', menu ? 'translate-x-0' : '-translate-x-full')}>
        <div className="flex items-center gap-2 px-4 py-5 text-lg font-bold"><Building2 size={20} /> Inovabens</div>
        <nav className="space-y-1 px-2">{nav.map((n) => (
          <Link key={n.href} href={n.href} onClick={() => setMenu(false)} className={clsx('flex items-center gap-2 rounded-md px-3 py-2 text-sm hover:bg-white/10', path.startsWith(n.href) && 'bg-white/15 font-medium')}><n.i size={16} />{n.t}</Link>))}
        </nav>
      </aside>
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between border-b bg-white px-4 py-3">
          <button className="btn-ghost md:hidden" onClick={() => setMenu((m) => !m)} aria-label="Menu">☰</button><span className="hidden md:block" />
          <div className="flex items-center gap-3 text-sm">
            <Link href="/alertas" className="relative p-1" aria-label="Alertas"><Bell size={18} />
              {!!alertas.data?.abertos && <span className="absolute -right-1 -top-1 rounded-full bg-red-600 px-1.5 text-[10px] font-bold text-white">{alertas.data.abertos}</span>}</Link>
            <Link href="/conta" className="text-slate-600 hover:underline">{usuario.nome} · <span className="text-slate-400">{usuario.perfil.nome}</span></Link>
            <button className="btn-ghost" onClick={async () => { await sair(); router.replace('/login'); }}><LogOut size={14} />Sair</button>
          </div>
        </header>
        <main className="mx-auto w-full max-w-7xl flex-1 p-4 md:p-6">{children}</main>
      </div>
    </div>
  );
}
