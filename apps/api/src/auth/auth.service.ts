import { HttpException, Inject, Injectable, Logger, UnauthorizedException, UnprocessableEntityException } from '@nestjs/common';
import { JwtService } from '@nestjs/jwt';
import { hash, verify } from '@node-rs/argon2';
import { and, eq, isNull } from 'drizzle-orm';
import { authenticator } from 'otplib';
import { randomUUID } from 'node:crypto';
import type { LoginResposta, Permissao } from '@inovabens/shared';
import { AuditService } from '../auditoria/audit.service';
import { decrypt, encrypt, hmac, tokenAleatorio } from '../common/crypto';
import { env } from '../config/env';
import { DB, Database } from '../db/db.module';
import { perfil_permissoes, perfis, permissoes, refresh_tokens, senha_resets, usuarios } from '../db/schema';

type Usuario = typeof usuarios.$inferSelect;
type Perfil = typeof perfis.$inferSelect;

authenticator.options = { window: 1 };
const MAX_FALHAS = 5;
let dummyHash: Promise<string> | undefined; // evita revelar (por tempo) se o e-mail existe

@Injectable()
export class AuthService {
  private readonly log = new Logger('Auth');
  constructor(@Inject(DB) private readonly db: Database, private readonly jwt: JwtService, private readonly audit: AuditService) {}

  private async permissoesDoPerfil(perfilId: string): Promise<Permissao[]> {
    const rows = await this.db.select({ codigo: permissoes.codigo }).from(perfil_permissoes)
      .innerJoin(permissoes, eq(permissoes.id, perfil_permissoes.permissao_id)).where(eq(perfil_permissoes.perfil_id, perfilId));
    return rows.map((r) => r.codigo as Permissao);
  }

  private async buscarPorEmail(email: string) {
    const [r] = await this.db.select({ u: usuarios, p: perfis }).from(usuarios).innerJoin(perfis, eq(perfis.id, usuarios.perfil_id))
      .where(and(eq(usuarios.email, email.toLowerCase()), isNull(usuarios.excluido_em))).limit(1);
    return r;
  }

  private async emitir(u: Usuario, p: Perfil, ip: string | null, familiaId?: string) {
    const perms = await this.permissoesDoPerfil(p.id);
    const expires_in = env().ACCESS_TTL_MIN * 60;
    const access_token = await this.jwt.signAsync(
      { sub: u.id, nome: u.nome, email: u.email, perfil_id: p.id, perfil_nome: p.nome, escopo_proprio: p.escopo_proprio, permissoes: perms },
      { secret: env().JWT_ACCESS_SECRET, expiresIn: expires_in },
    );
    const refresh = tokenAleatorio();
    await this.db.insert(refresh_tokens).values({
      usuario_id: u.id, familia_id: familiaId ?? randomUUID(), token_hash: hmac(refresh),
      expira_em: new Date(Date.now() + env().REFRESH_TTL_DAYS * 864e5), ip,
    });
    const resposta: LoginResposta = {
      access_token, expires_in,
      usuario: { id: u.id, nome: u.nome, email: u.email, perfil: { id: p.id, nome: p.nome, escopo_proprio: p.escopo_proprio }, permissoes: perms },
    };
    return { refresh, resposta };
  }

  async login(email: string, senha: string, totp: string | undefined, ip: string | null) {
    const r = await this.buscarPorEmail(email);
    if (!r) { await verify(await (dummyHash ??= hash('dummy-password')), senha).catch(() => false); throw new UnauthorizedException('Credenciais inválidas'); }
    const { u, p } = r;
    if (u.bloqueado_ate && u.bloqueado_ate > new Date()) {
      throw new HttpException('Conta temporariamente bloqueada por excesso de tentativas. Tente novamente mais tarde.', 429);
    }
    const ok = await verify(u.senha_hash, senha).catch(() => false);
    if (!ok || !u.ativo) {
      if (u.ativo) await this.registrarFalha(u);
      throw new UnauthorizedException('Credenciais inválidas');
    }
    if (u.totp_ativo) {
      if (!totp) throw new UnauthorizedException('TOTP_REQUIRED');
      if (!u.totp_secret || !authenticator.check(totp, decrypt(u.totp_secret))) { await this.registrarFalha(u); throw new UnauthorizedException('Código de verificação inválido'); }
    }
    await this.db.update(usuarios).set({ tentativas_falhas: 0, bloqueado_ate: null, ultimo_login_em: new Date() }).where(eq(usuarios.id, u.id));
    await this.audit.log({ usuario_id: u.id, entidade: 'usuarios', entidade_id: u.id, acao: 'LOGIN', ip });
    return this.emitir(u, p, ip);
  }

  /** Bloqueio progressivo: a partir da 5ª falha, 5 min, 10, 20... (máx. 24 h). */
  private async registrarFalha(u: Usuario) {
    const n = u.tentativas_falhas + 1;
    const bloqueado_ate = n >= MAX_FALHAS ? new Date(Date.now() + Math.min(5 * 2 ** (n - MAX_FALHAS), 1440) * 60_000) : null;
    await this.db.update(usuarios).set({ tentativas_falhas: n, bloqueado_ate }).where(eq(usuarios.id, u.id));
  }

  /** Rotaciona o refresh token. Reapresentar um token já usado revoga toda a cadeia (possível roubo). */
  async renovar(token: string | undefined, ip: string | null) {
    if (!token) throw new UnauthorizedException('Sessão ausente');
    const [rt] = await this.db.select().from(refresh_tokens).where(eq(refresh_tokens.token_hash, hmac(token))).limit(1);
    if (!rt) throw new UnauthorizedException('Sessão inválida');
    if (rt.revogado_em) {
      // janela de 10 s tolera requisições concorrentes legítimas (duas abas); fora dela, é reuso suspeito
      if (Date.now() - rt.revogado_em.getTime() > 10_000) {
        await this.db.update(refresh_tokens).set({ revogado_em: new Date() }).where(and(eq(refresh_tokens.familia_id, rt.familia_id), isNull(refresh_tokens.revogado_em)));
        this.log.warn(`Reuso de refresh token detectado (usuário ${rt.usuario_id}); sessão revogada.`);
      }
      throw new UnauthorizedException('Sessão inválida');
    }
    if (rt.expira_em < new Date()) throw new UnauthorizedException('Sessão expirada');
    const [r] = await this.db.select({ u: usuarios, p: perfis }).from(usuarios).innerJoin(perfis, eq(perfis.id, usuarios.perfil_id))
      .where(and(eq(usuarios.id, rt.usuario_id), isNull(usuarios.excluido_em))).limit(1);
    if (!r || !r.u.ativo) throw new UnauthorizedException('Sessão inválida');
    const revogado = await this.db.update(refresh_tokens).set({ revogado_em: new Date() })
      .where(and(eq(refresh_tokens.id, rt.id), isNull(refresh_tokens.revogado_em))).returning({ id: refresh_tokens.id });
    if (!revogado.length) throw new UnauthorizedException('Sessão inválida');
    return this.emitir(r.u, r.p, ip, rt.familia_id);
  }

  async logout(token: string | undefined) {
    if (!token) return;
    const [rt] = await this.db.select().from(refresh_tokens).where(eq(refresh_tokens.token_hash, hmac(token))).limit(1);
    if (rt) await this.db.update(refresh_tokens).set({ revogado_em: new Date() }).where(and(eq(refresh_tokens.familia_id, rt.familia_id), isNull(refresh_tokens.revogado_em)));
  }

  async revogarTodas(usuarioId: string) {
    await this.db.update(refresh_tokens).set({ revogado_em: new Date() }).where(and(eq(refresh_tokens.usuario_id, usuarioId), isNull(refresh_tokens.revogado_em)));
  }

  async alterarSenha(userId: string, atual: string, nova: string, ip: string | null) {
    const [u] = await this.db.select().from(usuarios).where(eq(usuarios.id, userId));
    if (!u || !(await verify(u.senha_hash, atual).catch(() => false))) throw new UnprocessableEntityException({ errors: { senha_atual: ['Senha atual incorreta'] } });
    await this.db.update(usuarios).set({ senha_hash: await hash(nova) }).where(eq(usuarios.id, userId));
    await this.revogarTodas(userId);
    await this.audit.log({ usuario_id: userId, entidade: 'usuarios', entidade_id: userId, acao: 'SENHA', ip });
  }

  /** Sempre responde igual (não revela se o e-mail existe). Sem SMTP o link só é logado fora de produção. */
  async esqueciSenha(email: string) {
    const r = await this.buscarPorEmail(email);
    if (!r || !r.u.ativo) return;
    const token = tokenAleatorio(32);
    await this.db.insert(senha_resets).values({ usuario_id: r.u.id, token_hash: hmac(token), expira_em: new Date(Date.now() + 3600_000) });
    if (env().NODE_ENV !== 'production') this.log.warn(`Link de redefinição (DEV): ${env().WEB_URL}/redefinir-senha?token=${token}`);
    else this.log.warn('Redefinição solicitada, mas envio de e-mail não está configurado: peça a um administrador para redefinir a senha.');
  }

  async redefinirSenha(token: string, nova: string) {
    const [rs] = await this.db.select().from(senha_resets).where(eq(senha_resets.token_hash, hmac(token))).limit(1);
    if (!rs || rs.usado_em || rs.expira_em < new Date()) throw new UnprocessableEntityException({ errors: { token: ['Link inválido ou expirado'] } });
    await this.db.update(usuarios).set({ senha_hash: await hash(nova), tentativas_falhas: 0, bloqueado_ate: null }).where(eq(usuarios.id, rs.usuario_id));
    await this.db.update(senha_resets).set({ usado_em: new Date() }).where(eq(senha_resets.id, rs.id));
    await this.revogarTodas(rs.usuario_id);
  }

  /* ----------------------------- 2FA (TOTP) ----------------------------- */
  async totpSetup(userId: string) {
    const [u] = await this.db.select().from(usuarios).where(eq(usuarios.id, userId));
    if (u.totp_ativo) throw new UnprocessableEntityException({ errors: { _: ['2FA já está ativo'] } });
    const secret = authenticator.generateSecret();
    await this.db.update(usuarios).set({ totp_secret: encrypt(secret) }).where(eq(usuarios.id, userId));
    return { otpauth_url: authenticator.keyuri(u.email, 'Inovabens', secret), secret };
  }
  async totpAtivar(userId: string, codigo: string) {
    const [u] = await this.db.select().from(usuarios).where(eq(usuarios.id, userId));
    if (!u.totp_secret || !authenticator.check(codigo, decrypt(u.totp_secret))) throw new UnprocessableEntityException({ errors: { codigo: ['Código inválido'] } });
    await this.db.update(usuarios).set({ totp_ativo: true }).where(eq(usuarios.id, userId));
  }
  async totpDesativar(userId: string, senha: string, codigo?: string) {
    const [u] = await this.db.select().from(usuarios).where(eq(usuarios.id, userId));
    if (!(await verify(u.senha_hash, senha).catch(() => false))) throw new UnprocessableEntityException({ errors: { senha: ['Senha incorreta'] } });
    if (u.totp_ativo && (!codigo || !u.totp_secret || !authenticator.check(codigo, decrypt(u.totp_secret)))) throw new UnprocessableEntityException({ errors: { codigo: ['Código inválido'] } });
    await this.db.update(usuarios).set({ totp_ativo: false, totp_secret: null }).where(eq(usuarios.id, userId));
  }
}
