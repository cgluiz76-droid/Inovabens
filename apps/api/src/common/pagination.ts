import { z } from 'zod';

export const paginacaoSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(20),
});
export type Paginacao = z.infer<typeof paginacaoSchema>;
export const offset = (p: Paginacao) => (p.page - 1) * p.pageSize;
export const paginado = <T>(data: T[], total: number, p: Paginacao) => ({ data, total, page: p.page, pageSize: p.pageSize });
