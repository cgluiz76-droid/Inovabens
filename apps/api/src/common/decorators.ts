import { createParamDecorator, ExecutionContext, SetMetadata } from '@nestjs/common';
import type { Permissao } from '@inovabens/shared';
import type { AuthUser } from './auth.types';

export const IS_PUBLIC = 'isPublic';
export const PERMISSIONS_KEY = 'permissions';

/** Rota sem autenticação (login, health, webhooks). */
export const Public = () => SetMetadata(IS_PUBLIC, true);
/** Exige TODAS as permissões listadas. Rotas sem este decorator exigem apenas estar autenticado. */
export const RequirePermission = (...p: Permissao[]) => SetMetadata(PERMISSIONS_KEY, p);
/** Injeta o usuário autenticado. */
export const CurrentUser = createParamDecorator((_: unknown, ctx: ExecutionContext): AuthUser => ctx.switchToHttp().getRequest().user);
/** IP do cliente (para auditoria). */
export const ClientIp = createParamDecorator((_: unknown, ctx: ExecutionContext): string => {
  const r = ctx.switchToHttp().getRequest();
  return (r.headers['x-forwarded-for']?.toString().split(',')[0] ?? r.ip ?? '').trim();
});
