import { CanActivate, ExecutionContext, ForbiddenException, Injectable } from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import type { Request } from 'express';
import type { Permissao } from '@inovabens/shared';
import { PERMISSIONS_KEY } from '../common/decorators';

/** Exige TODAS as permissões declaradas em @RequirePermission na rota. */
@Injectable()
export class PermissionsGuard implements CanActivate {
  constructor(private readonly reflector: Reflector) {}

  canActivate(ctx: ExecutionContext): boolean {
    const exigidas = this.reflector.getAllAndOverride<Permissao[]>(PERMISSIONS_KEY, [ctx.getHandler(), ctx.getClass()]);
    if (!exigidas?.length) return true;
    const user = ctx.switchToHttp().getRequest<Request>().user;
    if (!user || !exigidas.every((p) => user.permissoes.includes(p))) throw new ForbiddenException('Permissão insuficiente');
    return true;
  }
}
