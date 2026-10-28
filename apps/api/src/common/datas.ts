const TZ = 'America/Sao_Paulo';
/** Data de hoje (AAAA-MM-DD) no fuso de Brasília — o negócio opera nesse fuso. */
export const hojeBR = () => new Date().toLocaleDateString('sv-SE', { timeZone: TZ });
export const mesAtualBR = () => hojeBR().slice(0, 7);
export const diasEntre = (a: string, b: string) => Math.round((Date.parse(b) - Date.parse(a)) / 864e5);
export const fmtData = (d: string | Date | null | undefined) => {
  if (!d) return '—';
  const s = typeof d === 'string' ? d : d.toISOString();
  return `${s.slice(8, 10)}/${s.slice(5, 7)}/${s.slice(0, 4)}`;
};
export const fmtDataHora = (d: Date | null | undefined) =>
  d ? d.toLocaleString('pt-BR', { timeZone: TZ, dateStyle: 'short', timeStyle: 'short' }) : '—';
