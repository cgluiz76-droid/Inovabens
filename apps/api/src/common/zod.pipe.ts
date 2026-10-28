import { PipeTransform, UnprocessableEntityException } from '@nestjs/common';
import { ZodSchema } from 'zod';

/** Valida body/query com Zod → 422 no formato { status, title, errors: { campo: [msgs] } }. */
export class ZodPipe<T> implements PipeTransform<unknown, T> {
  constructor(private readonly schema: ZodSchema<T>) {}
  transform(value: unknown): T {
    const r = this.schema.safeParse(value);
    if (r.success) return r.data;
    const errors: Record<string, string[]> = {};
    for (const i of r.error.issues) (errors[i.path.join('.') || '_'] ??= []).push(i.message);
    throw new UnprocessableEntityException({ errors });
  }
}
