import type { NextRequest } from 'next/server';

// Proxy de mesma origem (em tempo de execução): o cookie de refresh fica first-party e a URL da API
// vem de variável de ambiente, sem precisar de rebuild ao trocar de ambiente.
export const dynamic = 'force-dynamic';
const alvo = () => {
  const u = (process.env.API_INTERNAL_URL ?? process.env.API_URL ?? 'http://localhost:3001').replace(/\/$/, '');
  return /^https?:\/\//.test(u) ? u : `http://${u}`; // aceita "host:porta" (ex.: rede interna do Render)
};
const REMOVER_REQ = ['host', 'connection', 'content-length', 'accept-encoding'];
const REMOVER_RES = ['content-encoding', 'content-length', 'transfer-encoding', 'connection'];

async function proxy(req: NextRequest, ctx: { params: Promise<{ path: string[] }> }) {
  const { path } = await ctx.params;
  const url = `${alvo()}/api/v1/${path.join('/')}${req.nextUrl.search}`;
  const headers = new Headers(req.headers);
  REMOVER_REQ.forEach((h) => headers.delete(h));
  headers.set('x-forwarded-for', req.headers.get('x-forwarded-for') ?? '');
  const temCorpo = !['GET', 'HEAD'].includes(req.method);
  try {
    const r = await fetch(url, { method: req.method, headers, body: temCorpo ? await req.arrayBuffer() : undefined, redirect: 'manual', cache: 'no-store' });
    const h = new Headers();
    r.headers.forEach((v, k) => { if (!REMOVER_RES.includes(k) && k !== 'set-cookie') h.append(k, v); });
    r.headers.getSetCookie().forEach((c) => h.append('set-cookie', c));
    return new Response(r.status === 204 || r.status === 304 ? null : r.body, { status: r.status, headers: h });
  } catch {
    return Response.json({ status: 502, title: 'API indisponível' }, { status: 502 });
  }
}
export { proxy as GET, proxy as POST, proxy as PUT, proxy as PATCH, proxy as DELETE };
