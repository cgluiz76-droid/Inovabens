'use client';
import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { Building2 } from 'lucide-react';
import { ApiError, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { Campo, Erro } from '@/components/ui';

export default function Login() {
  const { entrar } = useAuth(); const router = useRouter();
  const [email, setEmail] = useState(''); const [senha, setSenha] = useState(''); const [totp, setTotp] = useState('');
  const [pedeTotp, setPedeTotp] = useState(false); const [erro, setErro] = useState<string | null>(null); const [busy, setBusy] = useState(false);
  async function enviar(e: React.FormEvent) {
    e.preventDefault(); setErro(null); setBusy(true);
    try { await entrar(email, senha, pedeTotp ? totp : undefined); router.replace('/dashboard'); }
    catch (err) {
      if (err instanceof ApiError && err.problem.title === 'TOTP_REQUIRED') { setPedeTotp(true); setErro('Informe o código do aplicativo autenticador.'); }
      else setErro(msgErro(err));
    } finally { setBusy(false); }
  }
  return (
    <main className="flex min-h-screen items-center justify-center bg-brand-900 p-4">
      <form onSubmit={enviar} className="w-full max-w-sm space-y-4 rounded-xl bg-white p-6 shadow-xl">
        <div className="flex items-center gap-2 text-brand-700"><Building2 /><span className="text-xl font-bold">Inovabens</span></div>
        <p className="text-sm text-slate-500">Gestão imobiliária e financeira</p>
        <Erro msg={erro} />
        <Campo label="E-mail"><input className="input" type="email" autoComplete="username" required value={email} onChange={(e) => setEmail(e.target.value)} /></Campo>
        <Campo label="Senha"><input className="input" type="password" autoComplete="current-password" required value={senha} onChange={(e) => setSenha(e.target.value)} /></Campo>
        {pedeTotp && <Campo label="Código 2FA"><input className="input" inputMode="numeric" maxLength={6} autoFocus required value={totp} onChange={(e) => setTotp(e.target.value)} /></Campo>}
        <button className="btn-primary w-full" disabled={busy}>{busy ? 'Entrando…' : 'Entrar'}</button>
      </form>
    </main>
  );
}
