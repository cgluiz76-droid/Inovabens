import type { Permissao } from '@inovabens/shared';

/** Usuário autenticado, anexado a `req.user` pelo JwtAuthGuard. */
export interface AuthUser {
  id: string;
  nome: string;
  email: string;
  perfil_id: string;
  perfil_nome: string;
  /** true = só enxerga registros pelos quais é responsável (Corretor) */
  escopo_proprio: boolean;
  permissoes: Permissao[];
}
declare global {
  // eslint-disable-next-line @typescript-eslint/no-namespace
  namespace Express { interface Request { user?: AuthUser } }
}
