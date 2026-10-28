'use client';
import type { ProblemDetails } from '@inovabens/shared';

const BASE = '/api/v1';
let accessToken: string | null = null;
let refreshing: Promise<boolean> | null = null;
export const setToken = (t: string | null) => { accessToken = t; };
export const getToken = () => accessToken;

export class ApiError extends Error {
  constructor(public status: number, public problem: ProblemDetails) { super(problem.title); }
  /** Erros de validação por campo (422). */
  get campos(): Record<string, string[]> { return this.problem.errors ?? {}; }
}

/** Renova o access token usando o cookie httpOnly; várias chamadas simultâneas compartilham uma só renovação. */
export function renovar(): Promise<boolean> {
  refreshing ??= (async () => {
    try {
      const r = await fetch(`${BASE}/auth/refresh`, { method: 'POST', credentials: 'same-origin' });
      if (!r.ok) { accessToken = null; return false; }
      accessToken = (await r.json()).access_token; return true;
    } catch { return false; } finally { setTimeout(() => { refreshing = null; }, 0); }
  })();
  return refreshing;
}

async function raw(method: string, url: string, body?: unknown, retry = true): Promise<Response> {
  const headers: Record<string, string> = {};
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  let payload: BodyInit | undefined;
  if (body instanceof FormData) payload = body;
  else if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  const r = await fetch(BASE + url, { method, headers, body: payload, credentials: 'same-origin' });
  if (r.status === 401 && retry && !url.startsWith('/auth/') && (await renovar())) return raw(method, url, body, false);
  return r;
}

async function req<T>(method: string, url: string, body?: unknown): Promise<T> {
  const r = await raw(method, url, body);
  if (!r.ok) {
    let p: ProblemDetails;
    try { p = await r.json(); } catch { p = { status: r.status, title: r.statusText || 'Erro' }; }
    if (r.status === 401 && typeof window !== 'undefined' && !url.startsWith('/auth/')) window.location.href = '/login';
    throw new ApiError(r.status, p);
  }
  return r.status === 204 ? (undefined as T) : r.json();
}
export const api = {
  get: <T>(u: string) => req<T>('GET', u),
  post: <T>(u: string, b?: unknown) => req<T>('POST', u, b ?? {}),
  put: <T>(u: string, b?: unknown) => req<T>('PUT', u, b ?? {}),
  patch: <T>(u: string, b?: unknown) => req<T>('PATCH', u, b ?? {}),
  del: <T = void>(u: string) => req<T>('DELETE', u),
  /** Baixa um arquivo autenticado (PDF/Excel) e dispara o download no navegador. */
  async baixar(url: string, nome: string) {
    const r = await raw('GET', url);
    if (!r.ok) throw new ApiError(r.status, { status: r.status, title: 'Não foi possível gerar o arquivo' });
    const cd = /filename="([^"]+)"/.exec(r.headers.get('content-disposition') ?? '')?.[1] ?? nome;
    const a = document.createElement('a'); a.href = URL.createObjectURL(await r.blob()); a.download = cd; a.click(); URL.revokeObjectURL(a.href);
  },
};
export const qs = (o: Record<string, string | number | undefined | null>) => {
  const p = new URLSearchParams(); Object.entries(o).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') p.set(k, String(v)); });
  const s = p.toString(); return s ? `?${s}` : '';
};
export const msgErro = (e: unknown) => {
  if (e instanceof ApiError) { const c = Object.values(e.campos).flat(); return c.length ? c.join(' · ') : e.problem.detail ? `${e.problem.title}: ${e.problem.detail}` : e.problem.title; }
  return 'Erro inesperado';
};
