import { BadRequestException, Inject, Injectable, Logger, ServiceUnavailableException } from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import { Cron } from '@nestjs/schedule';
import { and, eq, isNull, lt, sql } from 'drizzle-orm';
import { randomUUID } from 'node:crypto';
import { decrypt, encrypt, hmac } from '../common/crypto';
import { env } from '../config/env';
import { DB, Database } from '../db/db.module';
import { alertas, compromisso_participantes, compromissos, google_contas, imoveis } from '../db/schema';

const API = 'https://www.googleapis.com/calendar/v3';
const TZ = 'America/Sao_Paulo';
type Conta = typeof google_contas.$inferSelect;
type Compromisso = typeof compromissos.$inferSelect;
interface EventoGoogle {
  id: string; etag?: string; status?: string; summary?: string; description?: string; location?: string; updated?: string;
  start?: { dateTime?: string; date?: string }; end?: { dateTime?: string; date?: string };
  extendedProperties?: { private?: Record<string, string> };
}

/* -------- mapeamentos puros (testáveis sem rede) -------- */
export function paraEventoGoogle(c: Compromisso, imovelCodigo: string, emails: string[]) {
  return {
    summary: c.titulo,
    description: [c.descricao, `Imóvel ${imovelCodigo} (#${imovelCodigo})`].filter(Boolean).join('\n\n'),
    location: c.local ?? undefined,
    start: { dateTime: c.inicio_em.toISOString(), timeZone: TZ },
    end: { dateTime: c.fim_em.toISOString(), timeZone: TZ },
    colorId: c.status === 'CONCLUIDO' ? '2' : undefined,
    attendees: emails.length ? emails.map((email) => ({ email })) : undefined,
    extendedProperties: { private: { inovabens_compromisso_id: c.id } },
  };
}
const quando = (x?: { dateTime?: string; date?: string }, fim = false) =>
  x?.dateTime ? new Date(x.dateTime) : x?.date ? new Date(new Date(`${x.date}T00:00:00-03:00`).getTime() - (fim ? 1000 : 0)) : null; // end.date do Google é exclusivo
export function deEventoGoogle(ev: EventoGoogle) {
  const inicio = quando(ev.start), fim = quando(ev.end, true);
  return inicio && fim ? { titulo: (ev.summary ?? '(sem título)').slice(0, 200), local: ev.location?.slice(0, 200) ?? null, inicio_em: inicio, fim_em: fim < inicio ? inicio : fim } : null;
}
/** Evento criado direto no Google é importado se o título/descrição citar o código do imóvel (ex.: "Visita #IMV-000012"). */
export const codigoImovelNoTexto = (ev: EventoGoogle) => /#(IMV-\d{4,})/i.exec(`${ev.summary ?? ''} ${ev.description ?? ''}`)?.[1]?.toUpperCase() ?? null;

@Injectable()
export class GoogleService {
  private readonly log = new Logger('Google');
  private readonly tokens = new Map<string, { token: string; exp: number }>();
  constructor(@Inject(DB) private readonly db: Database, private readonly jwt: JwtService) {}

  get configurado() { const e = env(); return !!(e.GOOGLE_CLIENT_ID && e.GOOGLE_CLIENT_SECRET && e.GOOGLE_REDIRECT_URI); }
  private exigir() { if (!this.configurado) throw new ServiceUnavailableException('Integração com o Google não configurada (GOOGLE_CLIENT_ID/SECRET/REDIRECT_URI).'); }
  private contaDe = async (usuarioId: string) => (await this.db.select().from(google_contas).where(eq(google_contas.usuario_id, usuarioId)))[0];

  async status(usuarioId: string) {
    const c = await this.contaDe(usuarioId);
    return { configurado: this.configurado, conectado: !!c, google_email: c?.google_email ?? null, ultima_sync_em: c?.ultima_sync_em ?? null };
  }

  /* ------------------------------- OAuth ------------------------------- */
  async urlAutorizacao(usuarioId: string) {
    this.exigir();
    const e = env();
    const state = await this.jwt.signAsync({ sub: usuarioId, p: 'google' }, { secret: e.JWT_ACCESS_SECRET, expiresIn: 600 });
    const p = new URLSearchParams({
      client_id: e.GOOGLE_CLIENT_ID!, redirect_uri: e.GOOGLE_REDIRECT_URI!, response_type: 'code',
      scope: 'https://www.googleapis.com/auth/calendar.events openid email', access_type: 'offline', prompt: 'consent', state,
    });
    return { url: `https://accounts.google.com/o/oauth2/v2/auth?${p}` };
  }

  async callback(code: string, state: string): Promise<string> {
    this.exigir();
    const e = env();
    let usuarioId: string;
    try { usuarioId = (await this.jwt.verifyAsync(state, { secret: e.JWT_ACCESS_SECRET })).sub; } catch { throw new BadRequestException('Estado OAuth inválido ou expirado'); }
    const r = await fetch('https://oauth2.googleapis.com/token', {
      method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ code, client_id: e.GOOGLE_CLIENT_ID!, client_secret: e.GOOGLE_CLIENT_SECRET!, redirect_uri: e.GOOGLE_REDIRECT_URI!, grant_type: 'authorization_code' }),
    });
    const t = (await r.json()) as { refresh_token?: string; id_token?: string; error_description?: string };
    if (!r.ok || !t.refresh_token) throw new BadRequestException(t.error_description ?? 'O Google não devolveu permissão offline. Revogue o acesso e tente de novo.');
    const email = t.id_token ? (JSON.parse(Buffer.from(t.id_token.split('.')[1], 'base64url').toString()) as { email?: string }).email ?? null : null;
    await this.db.insert(google_contas).values({ usuario_id: usuarioId, google_email: email, refresh_token_cifrado: encrypt(t.refresh_token), sync_token: null })
      .onConflictDoUpdate({ target: google_contas.usuario_id, set: { google_email: email, refresh_token_cifrado: encrypt(t.refresh_token), sync_token: null, atualizado_em: new Date() } });
    const conta = (await this.contaDe(usuarioId))!;
    void this.iniciarWatch(conta).catch((x) => this.log.warn(`watch: ${x}`));
    void this.sincronizar(usuarioId).catch((x) => this.log.warn(`sync inicial: ${x}`));
    return `${e.WEB_URL}/agenda?google=ok`;
  }

  async desconectar(usuarioId: string) {
    const conta = await this.contaDe(usuarioId);
    if (!conta) return;
    await this.pararWatch(conta).catch(() => undefined);
    await fetch(`https://oauth2.googleapis.com/revoke?token=${encodeURIComponent(decrypt(conta.refresh_token_cifrado))}`, { method: 'POST' }).catch(() => undefined);
    await this.db.delete(google_contas).where(eq(google_contas.id, conta.id));
    this.tokens.delete(conta.id);
  }

  private async accessToken(conta: Conta): Promise<string> {
    const c = this.tokens.get(conta.id);
    if (c && c.exp > Date.now() + 60_000) return c.token;
    const e = env();
    const r = await fetch('https://oauth2.googleapis.com/token', {
      method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ client_id: e.GOOGLE_CLIENT_ID!, client_secret: e.GOOGLE_CLIENT_SECRET!, refresh_token: decrypt(conta.refresh_token_cifrado), grant_type: 'refresh_token' }),
    });
    const t = (await r.json()) as { access_token?: string; expires_in?: number };
    if (!r.ok || !t.access_token) throw new Error('Não foi possível renovar o acesso ao Google (reconecte a conta).');
    this.tokens.set(conta.id, { token: t.access_token, exp: Date.now() + (t.expires_in ?? 3600) * 1000 });
    return t.access_token;
  }

  private async chamar<T = unknown>(conta: Conta, method: string, caminho: string, corpo?: unknown): Promise<{ status: number; data: T }> {
    const r = await fetch(`${API}/calendars/${encodeURIComponent(conta.calendar_id)}${caminho}`, {
      method, headers: { authorization: `Bearer ${await this.accessToken(conta)}`, 'content-type': 'application/json' },
      body: corpo ? JSON.stringify(corpo) : undefined, signal: AbortSignal.timeout(30_000),
    });
    const data = (r.status === 204 ? null : await r.json().catch(() => null)) as T;
    if (!r.ok && r.status !== 410 && r.status !== 404) throw new Error(`Google Calendar ${method} ${caminho}: HTTP ${r.status}`);
    return { status: r.status, data };
  }

  /* ----------------------- Sistema → Google (push) ----------------------- */
  async empurrar(compromissoId: string) {
    if (!this.configurado) return;
    const [x] = await this.db.select({ c: compromissos, codigo: imoveis.codigo }).from(compromissos).innerJoin(imoveis, eq(imoveis.id, compromissos.imovel_id)).where(eq(compromissos.id, compromissoId));
    if (!x) return;
    const conta = await this.contaDe(x.c.responsavel_id);
    if (!conta) return;
    if (x.c.status === 'CANCELADO' || x.c.excluido_em) { await this.removerEvento(conta, x.c.google_event_id); return; }
    const emails = (await this.db.select({ e: compromisso_participantes.email }).from(compromisso_participantes).where(eq(compromisso_participantes.compromisso_id, compromissoId))).flatMap((r) => (r.e ? [r.e] : []));
    const corpo = paraEventoGoogle(x.c, x.codigo, emails);
    const res = x.c.google_event_id
      ? await this.chamar<EventoGoogle>(conta, 'PUT', `/events/${encodeURIComponent(x.c.google_event_id)}`, corpo)
      : await this.chamar<EventoGoogle>(conta, 'POST', '/events', corpo);
    if (res.status === 404 && x.c.google_event_id) { // apagado no Google: recria
      const novo = await this.chamar<EventoGoogle>(conta, 'POST', '/events', corpo);
      await this.gravarVinculo(compromissoId, novo.data);
    } else await this.gravarVinculo(compromissoId, res.data);
  }
  private gravarVinculo(id: string, ev: EventoGoogle) {
    return this.db.update(compromissos).set({ google_event_id: ev.id, google_etag: ev.etag ?? null, sincronizado_em: new Date() }).where(eq(compromissos.id, id));
  }
  async removerEvento(conta: Conta, eventId: string | null) {
    if (eventId) await this.chamar(conta, 'DELETE', `/events/${encodeURIComponent(eventId)}`);
  }
  async removerDoGoogle(responsavelId: string, eventId: string | null) {
    const conta = await this.contaDe(responsavelId);
    if (conta && eventId && this.configurado) await this.removerEvento(conta, eventId);
  }

  /* ----------------------- Google → Sistema (pull) ----------------------- */
  /** Sincronização incremental por syncToken. Conflito: vence a alteração mais recente. */
  async sincronizar(usuarioId: string, tentativa = 0): Promise<{ importados: number; atualizados: number; cancelados: number }> {
    this.exigir();
    const conta = await this.contaDe(usuarioId);
    if (!conta) throw new BadRequestException('Conta Google não conectada');
    const out = { importados: 0, atualizados: 0, cancelados: 0 };
    let pageToken: string | undefined, nextSync: string | undefined;
    do {
      const p = new URLSearchParams({ showDeleted: 'true', singleEvents: 'true', maxResults: '250' });
      if (conta.sync_token) p.set('syncToken', conta.sync_token); else p.set('timeMin', new Date(Date.now() - 30 * 864e5).toISOString());
      if (pageToken) p.set('pageToken', pageToken);
      const r = await this.chamar<{ items?: EventoGoogle[]; nextPageToken?: string; nextSyncToken?: string }>(conta, 'GET', `/events?${p}`);
      if (r.status === 410) { // syncToken expirado: recomeça
        await this.db.update(google_contas).set({ sync_token: null }).where(eq(google_contas.id, conta.id));
        if (tentativa > 0) throw new Error('syncToken inválido repetidamente');
        return this.sincronizar(usuarioId, tentativa + 1);
      }
      for (const ev of r.data?.items ?? []) await this.aplicarEvento(conta, ev, out);
      pageToken = r.data?.nextPageToken; nextSync = r.data?.nextSyncToken ?? nextSync;
    } while (pageToken);
    await this.db.update(google_contas).set({ sync_token: nextSync ?? conta.sync_token, ultima_sync_em: new Date() }).where(eq(google_contas.id, conta.id));
    return out;
  }

  private async aplicarEvento(conta: Conta, ev: EventoGoogle, out: { importados: number; atualizados: number; cancelados: number }) {
    const idLocal = ev.extendedProperties?.private?.inovabens_compromisso_id;
    const [c] = await this.db.select().from(compromissos).where(and(
      eq(compromissos.responsavel_id, conta.usuario_id),
      idLocal ? eq(compromissos.id, idLocal) : eq(compromissos.google_event_id, ev.id),
    ));
    if (ev.status === 'cancelled') {
      if (c && c.status !== 'CANCELADO') {
        await this.db.update(compromissos).set({ status: 'CANCELADO', sincronizado_em: new Date() }).where(eq(compromissos.id, c.id));
        await this.db.update(alertas).set({ resolvido_em: new Date() }).where(and(eq(alertas.compromisso_id, c.id), isNull(alertas.resolvido_em)));
        out.cancelados++;
      }
      return;
    }
    const dados = deEventoGoogle(ev);
    if (!dados) return;
    if (c) {
      if (ev.etag && ev.etag === c.google_etag) return; // eco do nosso próprio push
      const googleMaisNovo = ev.updated ? new Date(ev.updated) > c.atualizado_em : true;
      if (googleMaisNovo) {
        await this.db.update(compromissos).set({ ...dados, google_event_id: ev.id, google_etag: ev.etag ?? null, sincronizado_em: new Date() }).where(eq(compromissos.id, c.id));
        if (dados.fim_em > new Date()) await this.db.update(alertas).set({ resolvido_em: new Date() }).where(and(eq(alertas.compromisso_id, c.id), isNull(alertas.resolvido_em)));
        out.atualizados++;
      } else { await this.empurrar(c.id); }
      return;
    }
    const codigo = codigoImovelNoTexto(ev);
    if (!codigo) return; // evento pessoal sem vínculo com imóvel: ignorado
    const [im] = await this.db.select({ id: imoveis.id }).from(imoveis).where(and(eq(imoveis.codigo, codigo), isNull(imoveis.excluido_em)));
    if (!im) return;
    await this.db.insert(compromissos).values({ ...dados, imovel_id: im.id, tipo: /visita/i.test(dados.titulo) ? 'VISITA' : /reuni/i.test(dados.titulo) ? 'REUNIAO' : 'TAREFA', responsavel_id: conta.usuario_id, google_event_id: ev.id, google_etag: ev.etag ?? null, sincronizado_em: new Date() }).onConflictDoNothing();
    out.importados++;
  }

  /* --------------------- Webhook (push notifications) --------------------- */
  private async iniciarWatch(conta: Conta) {
    const url = env().GOOGLE_WEBHOOK_URL;
    if (!url || !this.configurado) return;
    const canal = randomUUID();
    const r = await this.chamar<{ resourceId?: string; expiration?: string }>(conta, 'POST', '/events/watch', { id: canal, type: 'web_hook', address: url, token: hmac(canal) });
    await this.db.update(google_contas).set({ canal_watch_id: canal, canal_resource_id: r.data?.resourceId ?? null, canal_expira_em: r.data?.expiration ? new Date(Number(r.data.expiration)) : null }).where(eq(google_contas.id, conta.id));
  }
  private async pararWatch(conta: Conta) {
    if (!conta.canal_watch_id) return;
    const token = await this.accessToken(conta);
    await fetch(`${API.replace('/calendar/v3', '')}/calendar/v3/channels/stop`, { method: 'POST', headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: JSON.stringify({ id: conta.canal_watch_id, resourceId: conta.canal_resource_id }) });
  }
  /** O Google chama sem autenticação nossa: validamos o token do canal (HMAC do id do canal). */
  async webhook(canalId: string | undefined, token: string | undefined, estado: string | undefined) {
    if (!canalId || !token || token !== hmac(canalId) || estado === 'sync') return;
    const [conta] = await this.db.select().from(google_contas).where(eq(google_contas.canal_watch_id, canalId));
    if (conta) void this.sincronizar(conta.usuario_id).catch((e) => this.log.warn(`webhook sync: ${e}`));
  }

  /* ------------------------------- Jobs ------------------------------- */
  /** Rede de segurança caso o webhook falhe/não esteja configurado. */
  @Cron('*/10 * * * *', { timeZone: TZ })
  async jobSync() {
    if (env().DISABLE_JOBS || !this.configurado) return;
    for (const c of await this.db.select().from(google_contas)) {
      await this.sincronizar(c.usuario_id).catch((e) => this.log.warn(`sync ${c.google_email}: ${e}`));
    }
  }
  @Cron('0 4 * * *', { timeZone: TZ })
  async jobRenovarWatch() {
    if (env().DISABLE_JOBS || !this.configurado || !env().GOOGLE_WEBHOOK_URL) return;
    const limite = new Date(Date.now() + 2 * 864e5);
    for (const c of await this.db.select().from(google_contas).where(sql`${google_contas.canal_expira_em} is null or ${lt(google_contas.canal_expira_em, limite)}`)) {
      await this.iniciarWatch(c).catch((e) => this.log.warn(`renovar watch: ${e}`));
    }
  }
}
