import { z } from 'zod';

/** '' (campo vazio de formulário) vira null; mantém opcional/nulo. */
export const blank = <T extends z.ZodTypeAny>(s: T) => z.preprocess((v) => (v === '' ? null : v), s.nullish());

export const uuid = z.string().uuid();
export const dataISO = z.string().regex(/^\d{4}-\d{2}-\d{2}$/, 'Data inválida (use AAAA-MM-DD)');
export const mesISO = z.string().regex(/^\d{4}-(0[1-9]|1[0-2])$/, 'Mês inválido (use AAAA-MM)');

/** Dinheiro: aceita "1234.5" ou número; devolve string com 2 casas. Nunca float no banco. */
export const dinheiro = z
  .union([z.string().trim().regex(/^-?\d+(\.\d{1,2})?$/, 'Valor inválido (ex.: 1234.50)'), z.number().finite()])
  .transform((v) => (typeof v === 'number' ? v.toFixed(2) : v));
export const dinheiroPositivo = dinheiro.refine((v) => !v.startsWith('-'), 'O valor não pode ser negativo');

/** Medidas (m²) com até 2 casas. */
export const medida = z
  .union([z.string().trim().regex(/^\d+(\.\d{1,2})?$/, 'Medida inválida'), z.number().nonnegative().finite()])
  .transform((v) => (typeof v === 'number' ? v.toFixed(2) : v));

/** Política de senha: >= 10 caracteres, com letra e número. */
export const senhaForte = z
  .string()
  .min(10, 'A senha precisa ter pelo menos 10 caracteres')
  .max(128)
  .regex(/[A-Za-z]/, 'A senha precisa ter ao menos uma letra')
  .regex(/\d/, 'A senha precisa ter ao menos um número');

/** Escapa curingas de LIKE para busca literal. */
export const likeLiteral = (s: string) => s.replace(/[\\%_]/g, (c) => `\\${c}`);
