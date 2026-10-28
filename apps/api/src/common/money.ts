/** Aritmética monetária exata em centavos (bigint). Nunca use float para dinheiro. */
export const toCents = (v: string | number | null | undefined): bigint => {
  if (v === null || v === undefined || v === '') return 0n;
  const s = typeof v === 'number' ? v.toFixed(2) : v.trim();
  const m = /^(-?)(\d+)(?:\.(\d{1,}))?$/.exec(s);
  if (!m) throw new Error(`Valor monetário inválido: ${v}`);
  const frac = ((m[3] ?? '') + '00').slice(0, 2);
  const c = BigInt(m[2]) * 100n + BigInt(frac);
  return m[1] === '-' ? -c : c;
};
export const fromCents = (c: bigint): string => {
  const neg = c < 0n; const a = neg ? -c : c;
  return `${neg ? '-' : ''}${a / 100n}.${(a % 100n).toString().padStart(2, '0')}`;
};
export const sumCents = (vs: Array<string | number | null | undefined>): bigint => vs.reduce<bigint>((a, v) => a + toCents(v), 0n);
export const brl = (v: string | number | null | undefined) =>
  new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(Number(fromCents(toCents(v))));
