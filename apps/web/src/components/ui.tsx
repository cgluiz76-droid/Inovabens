'use client';
import { useEffect, type ReactNode } from 'react';
import { X } from 'lucide-react';
import clsx from 'clsx';

export const Campo = ({ label, children, erro, className }: { label: string; children: ReactNode; erro?: string; className?: string }) => (
  <label className={clsx('block text-sm', className)}><span className="mb-1 block font-medium text-slate-600">{label}</span>{children}{erro && <span className="mt-1 block text-xs text-red-600">{erro}</span>}</label>
);
export function Modal({ titulo, onClose, children, largo }: { titulo: string; onClose: () => void; children: ReactNode; largo?: boolean }) {
  useEffect(() => { const f = (e: KeyboardEvent) => e.key === 'Escape' && onClose(); window.addEventListener('keydown', f); return () => window.removeEventListener('keydown', f); }, [onClose]);
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-slate-900/50 p-4" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={clsx('my-8 w-full rounded-lg bg-white p-5 shadow-xl', largo ? 'max-w-3xl' : 'max-w-lg')} role="dialog" aria-modal>
        <div className="mb-4 flex items-center justify-between"><h2 className="text-lg font-semibold">{titulo}</h2><button onClick={onClose} aria-label="Fechar"><X size={18} /></button></div>
        {children}
      </div>
    </div>
  );
}
const cores: Record<string, string> = {
  PAGO: 'bg-green-100 text-green-700', NAO_PAGO: 'bg-red-100 text-red-700', NAO_SE_APLICA: 'bg-slate-200 text-slate-600',
  PENDENTE: 'bg-amber-100 text-amber-700', CONCLUIDO: 'bg-green-100 text-green-700', CANCELADO: 'bg-slate-200 text-slate-600',
};
export const Badge = ({ children, k, cor }: { children: ReactNode; k?: string; cor?: string }) => (
  <span className={clsx('inline-block rounded-full px-2 py-0.5 text-xs font-medium', (k && cores[k]) || 'bg-brand-100 text-brand-700')} style={cor ? { backgroundColor: `${cor}22`, color: cor } : undefined}>{children}</span>
);
export const Erro = ({ msg }: { msg?: string | null }) => (msg ? <p className="rounded-md bg-red-50 px-3 py-2 text-sm text-red-700" role="alert">{msg}</p> : null);
export const Vazio = ({ children }: { children: ReactNode }) => <p className="py-8 text-center text-sm text-slate-500">{children}</p>;
export const Carregando = () => <p className="py-8 text-center text-sm text-slate-400">Carregando…</p>;
export const Titulo = ({ children, acoes }: { children: ReactNode; acoes?: ReactNode }) => (
  <div className="mb-4 flex flex-wrap items-center justify-between gap-2"><h1 className="text-xl font-semibold text-slate-900">{children}</h1><div className="flex gap-2">{acoes}</div></div>
);
export function Paginacao({ page, pageSize, total, onChange }: { page: number; pageSize: number; total: number; onChange: (p: number) => void }) {
  const ult = Math.max(1, Math.ceil(total / pageSize));
  return (
    <div className="mt-3 flex items-center justify-between text-sm text-slate-500"><span>{total} registro(s)</span>
      <div className="flex items-center gap-2"><button className="btn-ghost" disabled={page <= 1} onClick={() => onChange(page - 1)}>Anterior</button><span>{page}/{ult}</span><button className="btn-ghost" disabled={page >= ult} onClick={() => onChange(page + 1)}>Próxima</button></div></div>
  );
}
export const Select = ({ opcoes, ...p }: { opcoes: { v: string; t: string }[] } & React.SelectHTMLAttributes<HTMLSelectElement>) => (
  <select className="input" {...p}>{opcoes.map((o) => <option key={o.v} value={o.v}>{o.t}</option>)}</select>
);
