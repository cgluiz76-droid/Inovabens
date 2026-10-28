'use client';
import { useState } from 'react';
import { FINALIDADE_IMOVEL, SITUACAO_IMOVEL } from '@inovabens/shared';
import { useQuery } from '@tanstack/react-query';
import { api, ApiError, msgErro } from '@/lib/api';
import { rotulo } from '@/lib/fmt';
import { Campo, Erro, Select } from './ui';

const TIPOS = ['apartamento', 'casa', 'terreno', 'sala comercial', 'galpão', 'loja', 'fazenda/sítio', 'outro'];
type Dados = Record<string, string | boolean | number | null | undefined>;
const CAMPOS_TEXTO: [string, string, string?][] = [
  ['cep', 'CEP'], ['logradouro', 'Logradouro'], ['numero', 'Número'], ['complemento', 'Complemento'], ['bairro', 'Bairro'], ['cidade', 'Cidade'], ['uf', 'UF'],
  ['matricula', 'Matrícula'], ['cartorio', 'Cartório'], ['inscricao_municipal', 'Inscrição municipal'],
];
const CAMPOS_NUM: [string, string][] = [
  ['area_total_m2', 'Área total (m²)'], ['area_construida_m2', 'Área construída (m²)'], ['area_util_m2', 'Área útil (m²)'],
  ['quartos', 'Quartos'], ['suites', 'Suítes'], ['banheiros', 'Banheiros'], ['vagas', 'Vagas'], ['preco_venda', 'Preço de venda (R$)'], ['preco_locacao', 'Preço de locação (R$)'],
];
const INTEIROS = new Set(['quartos', 'suites', 'banheiros', 'vagas']);

export function ImovelForm({ inicial, onSalvar, rotuloBotao = 'Salvar' }: { inicial?: Dados; onSalvar: (corpo: Dados) => Promise<void>; rotuloBotao?: string }) {
  const [v, setV] = useState<Dados>({ finalidade: 'VENDA', situacao: 'CAPTACAO', tipo: 'apartamento', ...inicial });
  const [erro, setErro] = useState<string | null>(null); const [campos, setCampos] = useState<Record<string, string[]>>({}); const [busy, setBusy] = useState(false);
  const corretores = useQuery({ queryKey: ['users-options'], queryFn: () => api.get<{ id: string; nome: string }[]>('/users/options') });
  const set = (k: string, val: string | boolean) => setV((s) => ({ ...s, [k]: val }));
  const txt = (k: string) => (v[k] === null || v[k] === undefined ? '' : String(v[k]));
  async function enviar(e: React.FormEvent) {
    e.preventDefault(); setErro(null); setCampos({}); setBusy(true);
    const corpo: Dados = {};
    for (const k of ['titulo', 'tipo', 'finalidade', 'situacao', 'descricao_publica', 'descricao_interna', 'corretor_responsavel_id', ...CAMPOS_TEXTO.map((c) => c[0])]) corpo[k] = txt(k) === '' ? null : txt(k);
    for (const [k] of CAMPOS_NUM) corpo[k] = txt(k) === '' ? null : INTEIROS.has(k) ? Number(txt(k)) : txt(k).replace(',', '.');
    corpo.divulgar_site = !!v.divulgar_site; corpo.endereco_publico = !!v.endereco_publico;
    try { await onSalvar(corpo); } catch (err) { if (err instanceof ApiError) setCampos(err.campos); setErro(msgErro(err)); } finally { setBusy(false); }
  }
  const e1 = (k: string) => campos[k]?.[0];
  return (
    <form onSubmit={enviar} className="space-y-4">
      <Erro msg={erro} />
      <div className="grid gap-3 md:grid-cols-4">
        <Campo label="Título *" className="md:col-span-2" erro={e1('titulo')}><input className="input" required value={txt('titulo')} onChange={(e) => set('titulo', e.target.value)} /></Campo>
        <Campo label="Tipo"><input className="input" list="tipos" value={txt('tipo')} onChange={(e) => set('tipo', e.target.value)} /><datalist id="tipos">{TIPOS.map((t) => <option key={t} value={t} />)}</datalist></Campo>
        <Campo label="Corretor responsável"><Select value={txt('corretor_responsavel_id')} onChange={(e) => set('corretor_responsavel_id', e.target.value)} opcoes={[{ v: '', t: '(eu / automático)' }, ...(corretores.data ?? []).map((c) => ({ v: c.id, t: c.nome }))]} /></Campo>
        <Campo label="Finalidade"><Select value={txt('finalidade')} onChange={(e) => set('finalidade', e.target.value)} opcoes={FINALIDADE_IMOVEL.map((x) => ({ v: x, t: rotulo(x) }))} /></Campo>
        <Campo label="Situação"><Select value={txt('situacao')} onChange={(e) => set('situacao', e.target.value)} opcoes={SITUACAO_IMOVEL.map((x) => ({ v: x, t: rotulo(x) }))} /></Campo>
      </div>
      <div className="grid gap-3 md:grid-cols-4">{CAMPOS_TEXTO.map(([k, l]) => <Campo key={k} label={l} erro={e1(k)}><input className="input" value={txt(k)} onChange={(e) => set(k, e.target.value)} maxLength={k === 'uf' ? 2 : undefined} /></Campo>)}</div>
      <div className="grid gap-3 md:grid-cols-4">{CAMPOS_NUM.map(([k, l]) => <Campo key={k} label={l} erro={e1(k)}><input className="input" inputMode="decimal" value={txt(k)} onChange={(e) => set(k, e.target.value)} /></Campo>)}</div>
      <Campo label="Descrição pública"><textarea className="input" rows={3} value={txt('descricao_publica')} onChange={(e) => set('descricao_publica', e.target.value)} /></Campo>
      <Campo label="Observações internas (não aparecem no site)"><textarea className="input" rows={3} value={txt('descricao_interna')} onChange={(e) => set('descricao_interna', e.target.value)} /></Campo>
      <div className="flex flex-wrap gap-4 text-sm">
        <label className="flex items-center gap-2"><input type="checkbox" checked={!!v.divulgar_site} onChange={(e) => set('divulgar_site', e.target.checked)} />Divulgar no site</label>
        <label className="flex items-center gap-2"><input type="checkbox" checked={!!v.endereco_publico} onChange={(e) => set('endereco_publico', e.target.checked)} />Endereço público</label>
      </div>
      <button className="btn-primary" disabled={busy}>{busy ? 'Salvando…' : rotuloBotao}</button>
    </form>
  );
}
