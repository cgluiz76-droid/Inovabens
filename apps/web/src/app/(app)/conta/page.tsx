'use client';
import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { api, msgErro } from '@/lib/api';
import { useAuth } from '@/lib/auth';
import { Campo, Erro, Titulo } from '@/components/ui';

export default function Conta() {
  const { usuario } = useAuth();
  const [s, setS] = useState({ senha_atual: '', nova_senha: '' }); const [ok, setOk] = useState(false);
  const trocar = useMutation({ mutationFn: () => api.post('/auth/alterar-senha', s), onSuccess: () => { setOk(true); setS({ senha_atual: '', nova_senha: '' }); } });
  const [setup, setSetup] = useState<{ otpauth_url: string; secret: string } | null>(null); const [codigo, setCodigo] = useState(''); const [ativo, setAtivo] = useState(!!usuario?.totp_ativo);
  const iniciar = useMutation({ mutationFn: () => api.post<{ otpauth_url: string; secret: string }>('/auth/2fa/setup'), onSuccess: setSetup });
  const ativar = useMutation({ mutationFn: () => api.post('/auth/2fa/enable', { codigo }), onSuccess: () => { setAtivo(true); setSetup(null); setCodigo(''); } });
  return (
    <>
      <Titulo>Minha conta</Titulo>
      <div className="grid max-w-3xl gap-4">
        <section className="card"><h2 className="mb-1 font-semibold">{usuario?.nome}</h2><p className="text-sm text-slate-500">{usuario?.email} · perfil {usuario?.perfil.nome}</p></section>
        <form className="card space-y-3" onSubmit={(e) => { e.preventDefault(); setOk(false); trocar.mutate(); }}><h2 className="font-semibold">Alterar senha</h2><Erro msg={trocar.error ? msgErro(trocar.error) : null} />{ok && <p className="text-sm text-green-700">Senha alterada.</p>}
          <Campo label="Senha atual"><input className="input" type="password" autoComplete="current-password" required value={s.senha_atual} onChange={(e) => setS({ ...s, senha_atual: e.target.value })} /></Campo>
          <Campo label="Nova senha (mín. 10 caracteres, letras e números)"><input className="input" type="password" autoComplete="new-password" required value={s.nova_senha} onChange={(e) => setS({ ...s, nova_senha: e.target.value })} /></Campo>
          <button className="btn-primary" disabled={trocar.isPending}>Alterar senha</button></form>
        <section className="card space-y-3"><h2 className="font-semibold">Autenticação em dois fatores (2FA)</h2>
          {ativo ? <p className="text-sm text-green-700">2FA ativado. O código será pedido a cada login.</p> : !setup ? <button className="btn-ghost" onClick={() => iniciar.mutate()}>Configurar 2FA</button> : (
            <div className="space-y-2 text-sm"><Erro msg={ativar.error ? msgErro(ativar.error) : null} />
              <p>Cadastre esta chave no seu aplicativo autenticador (Google Authenticator, Authy…):</p><code className="block break-all rounded bg-slate-100 p-2">{setup.secret}</code>
              <a className="text-brand-600 underline" href={setup.otpauth_url}>Abrir no aplicativo autenticador</a>
              <div className="flex gap-2"><input className="input max-w-[160px]" inputMode="numeric" maxLength={6} placeholder="Código de 6 dígitos" value={codigo} onChange={(e) => setCodigo(e.target.value)} /><button className="btn-primary" disabled={codigo.length !== 6} onClick={() => ativar.mutate()}>Ativar</button></div></div>)}
        </section>
      </div>
    </>
  );
}
