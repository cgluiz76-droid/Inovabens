import { Body, Controller, Get, HttpCode, Post, Req, Res } from '@nestjs/common';
import { Throttle } from '@nestjs/throttler';
import type { Request, Response } from 'express';
import { z } from 'zod';
import type { UsuarioLogado } from '@inovabens/shared';
import type { AuthUser } from '../common/auth.types';
import { ClientIp, CurrentUser, Public } from '../common/decorators';
import { senhaForte } from '../common/schemas';
import { ZodPipe } from '../common/zod.pipe';
import { env } from '../config/env';
import { AuthService } from './auth.service';

const COOKIE = 'refresh_token';
const loginSchema = z.object({ email: z.string().trim().email(), senha: z.string().min(1).max(200), totp: z.string().trim().length(6).optional() });
const alterarSenhaSchema = z.object({ senha_atual: z.string().min(1), nova_senha: senhaForte });
const esqueciSchema = z.object({ email: z.string().trim().email() });
const resetSchema = z.object({ token: z.string().min(10).max(200), nova_senha: senhaForte });
const codigoSchema = z.object({ codigo: z.string().trim().length(6) });
const desativarSchema = z.object({ senha: z.string().min(1), codigo: z.string().trim().length(6).optional() });

@Controller('auth')
export class AuthController {
  constructor(private readonly auth: AuthService) {}

  private setCookie(res: Response, token: string) {
    res.cookie(COOKIE, token, {
      httpOnly: true, secure: env().COOKIE_SECURE, sameSite: 'lax', path: '/api/v1/auth', maxAge: env().REFRESH_TTL_DAYS * 864e5,
    });
  }

  @Public() @Throttle({ default: { limit: 10, ttl: 60_000 } }) @Post('login') @HttpCode(200)
  async login(@Body(new ZodPipe(loginSchema)) b: z.infer<typeof loginSchema>, @ClientIp() ip: string, @Res({ passthrough: true }) res: Response) {
    const { refresh, resposta } = await this.auth.login(b.email, b.senha, b.totp, ip || null);
    this.setCookie(res, refresh);
    return resposta;
  }

  @Public() @Throttle({ default: { limit: 30, ttl: 60_000 } }) @Post('refresh') @HttpCode(200)
  async refresh(@Req() req: Request, @ClientIp() ip: string, @Res({ passthrough: true }) res: Response) {
    const { refresh, resposta } = await this.auth.renovar(req.cookies?.[COOKIE], ip || null);
    this.setCookie(res, refresh);
    return resposta;
  }

  @Public() @Post('logout') @HttpCode(204)
  async logout(@Req() req: Request, @Res({ passthrough: true }) res: Response) {
    await this.auth.logout(req.cookies?.[COOKIE]);
    res.clearCookie(COOKIE, { path: '/api/v1/auth' });
  }

  @Get('me')
  me(@CurrentUser() u: AuthUser): UsuarioLogado {
    return { id: u.id, nome: u.nome, email: u.email, perfil: { id: u.perfil_id, nome: u.perfil_nome, escopo_proprio: u.escopo_proprio }, permissoes: u.permissoes };
  }

  @Post('alterar-senha') @HttpCode(204)
  alterarSenha(@CurrentUser() u: AuthUser, @Body(new ZodPipe(alterarSenhaSchema)) b: z.infer<typeof alterarSenhaSchema>, @ClientIp() ip: string) {
    return this.auth.alterarSenha(u.id, b.senha_atual, b.nova_senha, ip || null);
  }

  @Public() @Throttle({ default: { limit: 5, ttl: 60_000 } }) @Post('forgot-password') @HttpCode(204)
  forgot(@Body(new ZodPipe(esqueciSchema)) b: z.infer<typeof esqueciSchema>) { return this.auth.esqueciSenha(b.email); }

  @Public() @Throttle({ default: { limit: 10, ttl: 60_000 } }) @Post('reset-password') @HttpCode(204)
  reset(@Body(new ZodPipe(resetSchema)) b: z.infer<typeof resetSchema>) { return this.auth.redefinirSenha(b.token, b.nova_senha); }

  @Post('2fa/setup') @HttpCode(200)
  setup2fa(@CurrentUser() u: AuthUser) { return this.auth.totpSetup(u.id); }

  @Post('2fa/enable') @HttpCode(204)
  enable2fa(@CurrentUser() u: AuthUser, @Body(new ZodPipe(codigoSchema)) b: z.infer<typeof codigoSchema>) { return this.auth.totpAtivar(u.id, b.codigo); }

  @Post('2fa/disable') @HttpCode(204)
  disable2fa(@CurrentUser() u: AuthUser, @Body(new ZodPipe(desativarSchema)) b: z.infer<typeof desativarSchema>) { return this.auth.totpDesativar(u.id, b.senha, b.codigo); }
}
