import { ArgumentsHost, Catch, ExceptionFilter, HttpException, HttpStatus, Logger } from '@nestjs/common';
import type { Response } from 'express';

/** Formata todo erro como { status, title, detail?, errors? } (RFC 7807 simplificado). */
@Catch()
export class ProblemFilter implements ExceptionFilter {
  private readonly log = new Logger('Erro');
  catch(e: unknown, host: ArgumentsHost) {
    const res = host.switchToHttp().getResponse<Response>();
    let status = HttpStatus.INTERNAL_SERVER_ERROR;
    let title = 'Erro interno do servidor';
    let detail: string | undefined;
    let errors: Record<string, string[]> | undefined;

    if (e instanceof HttpException) {
      status = e.getStatus();
      const r = e.getResponse() as any;
      if (typeof r === 'string') { title = r; }
      else {
        title = typeof r.message === 'string' ? r.message : (r.error ?? e.message);
        if (Array.isArray(r.message)) detail = r.message.join('; ');
        if (r.errors) errors = r.errors;
        if (r.detail) detail = r.detail;
        if (r.title) title = r.title;
      }
    } else {
      // o Drizzle encapsula o erro do driver em `cause`
      const raw = e as { code?: string; constraint?: string; cause?: unknown };
      const pg = (raw?.code ? raw : (raw?.cause as typeof raw)) ?? raw;
      // Violações de constraint do PostgreSQL viram 409/422 legíveis
      if (pg?.code === '23505') { status = 409; title = 'Registro duplicado'; detail = pg.constraint; }
      else if (pg?.code === '23514') { status = 422; title = 'Regra de negócio violada'; detail = pg.constraint; }
      else if (pg?.code === '23503') { status = 409; title = 'Registro vinculado a outros dados'; detail = pg.constraint; }
      else this.log.error(e instanceof Error ? (e.stack ?? e.message) : String(e));
    }
    res.status(status).json({ status, title, ...(detail ? { detail } : {}), ...(errors ? { errors } : {}) });
  }
}
