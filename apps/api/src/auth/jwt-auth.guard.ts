import { CanActivate, ExecutionContext, Injectable, UnauthorizedException } from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import { JwtService } from '@nestjs/jwt';
import type { Request } from 'express';
import { IS_PUBLIC } from '../common/decorators';
import type { AuthUser } from '../common/auth.types';
import { env } from '../config/env';

/** Autentica toda rota (exceto @Public) via JWT de acesso curto e anexa `req.user`. */
@Injectable()
export class JwtAuthGuard implements CanActivate {
  constructor(private readonly reflector: Reflector, private readonly jwt: JwtService) {}

  async canActivate(ctx: ExecutionContext): Promise<boolean> {
    if (this.reflector.getAllAndOverride<boolean>(IS_PUBLIC, [ctx.getHandler(), ctx.getClass()])) return true;
    const req = ctx.switchToHttp().getRequest<Request>();
    const h = req.headers.authorization;
    if (!h?.startsWith('Bearer ')) throw new UnauthorizedException('Não autenticado');
    try {
      const p = await this.jwt.verifyAsync(h.slice(7), { secret: env().JWT_ACCESS_SECRET });
      req.user = {
        id: p.sub, nome: p.nome, email: p.email, perfil_id: p.perfil_id, perfil_nome: p.perfil_nome,
        escopo_proprio: !!p.escopo_proprio, permissoes: p.permissoes ?? [],
      } satisfies AuthUser;
      return true;
    } catch {
      throw new UnauthorizedException('Sessão expirada');
    }
  }
}
