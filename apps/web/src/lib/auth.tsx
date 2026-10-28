'use client';
import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { useRouter } from 'next/navigation';
import { api, renovar, setToken } from './api';

export interface Usuario { id: string; nome: string; email: string; perfil: { id: string; nome: string }; permissoes: string[]; escopo_proprio: boolean; totp_ativo?: boolean }
interface Ctx { usuario: Usuario | null; carregando: boolean; pode: (p: string) => boolean; entrar: (email: string, senha: string, totp?: string) => Promise<void>; sair: () => Promise<void> }
const AuthCtx = createContext<Ctx>(null as never);
export const useAuth = () => useContext(AuthCtx);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [usuario, setUsuario] = useState<Usuario | null>(null);
  const [carregando, setCarregando] = useState(true);
  useEffect(() => {
    (async () => {
      if (await renovar()) { try { setUsuario(await api.get<Usuario>('/auth/me')); } catch { /* sessão inválida */ } }
      setCarregando(false);
    })();
  }, []);
  const entrar = useCallback(async (email: string, senha: string, totp?: string) => {
    const r = await api.post<{ access_token: string; usuario: Usuario }>('/auth/login', { email, senha, ...(totp ? { totp } : {}) });
    setToken(r.access_token); setUsuario(r.usuario);
  }, []);
  const sair = useCallback(async () => { try { await api.post('/auth/logout'); } catch { /* ignore */ } setToken(null); setUsuario(null); }, []);
  const pode = useCallback((p: string) => !!usuario?.permissoes.includes(p), [usuario]);
  return <AuthCtx.Provider value={{ usuario, carregando, pode, entrar, sair }}>{children}</AuthCtx.Provider>;
}

/** Redireciona para /login quando não há sessão. */
export function useExigirLogin() {
  const a = useAuth(); const r = useRouter();
  useEffect(() => { if (!a.carregando && !a.usuario) r.replace('/login'); }, [a.carregando, a.usuario, r]);
  return a;
}
