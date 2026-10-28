export const brl = (v: string | number | null | undefined) => (v === null || v === undefined || v === '' ? '—' : Number(v).toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' }));
export const pct = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${(v * 100).toLocaleString('pt-BR', { maximumFractionDigits: 2 })}%`);
export const data = (v?: string | null) => (v ? new Date(v.length === 10 ? `${v}T12:00:00` : v).toLocaleDateString('pt-BR') : '—');
export const dataHora = (v?: string | null) => (v ? new Date(v).toLocaleString('pt-BR', { dateStyle: 'short', timeStyle: 'short' }) : '—');
const ACENTOS: Record<string, string> = {
  captacao: 'captação', aquisicao: 'aquisição', regularizacao: 'regularização', locacao: 'locação', negociacao: 'negociação', documentacao: 'documentação',
  retificacao: 'retificação', area: 'área', nao: 'não', concluido: 'concluído', reuniao: 'reunião', acao: 'ação', estrategica: 'estratégica', email: 'e-mail',
};
export const rotulo = (s?: string | null) => {
  if (!s) return '—';
  const t = s.toLowerCase().split('_').map((w) => ACENTOS[w] ?? w).join(' ');
  return t.charAt(0).toUpperCase() + t.slice(1);
};
export const mesAtual = () => new Date().toISOString().slice(0, 7);
export const paraInputLocal = (iso: string) => { const d = new Date(iso); d.setMinutes(d.getMinutes() - d.getTimezoneOffset()); return d.toISOString().slice(0, 16); };
