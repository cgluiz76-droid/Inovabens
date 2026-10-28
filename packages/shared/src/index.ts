import { z } from 'zod';

/* ------------------------- Enums (espelham o banco) ------------------------- */
export const TIPO_PESSOA = ['PF', 'PJ'] as const;
export const PAPEL_PESSOA = ['CLIENTE', 'PROPRIETARIO', 'FIADOR', 'PARCEIRO', 'PRESTADOR', 'TERCEIRO'] as const;
export const FINALIDADE_IMOVEL = ['VENDA', 'LOCACAO', 'AMBOS'] as const;
export const SITUACAO_IMOVEL = ['CAPTACAO', 'EM_AQUISICAO', 'EM_REGULARIZACAO', 'EM_RETROFIT', 'DISPONIVEL', 'VENDIDO', 'LOCADO', 'ARQUIVADO'] as const;
export const TIPO_MIDIA = ['FOTO', 'VIDEO', 'PLANTA', 'DOCUMENTO'] as const;
export const TIPO_REGULARIZACAO = ['DOCUMENTACAO', 'TRIBUTOS_ATRASO', 'DESMEMBRAMENTO', 'FRACIONAMENTO', 'RETIFICACAO_AREA', 'OUTRO'] as const;
export const STATUS_REGULARIZACAO = ['NAO_INICIADA', 'EM_ANDAMENTO', 'CONCLUIDA', 'CANCELADA'] as const;
export const TIPO_ITEM_AQUISICAO = ['IMPOSTO', 'TAXA', 'DESPESA'] as const;
export const RECORRENCIA = ['UNICA', 'MENSAL', 'ANUAL'] as const;
export const STATUS_LANCAMENTO = ['PAGO', 'NAO_PAGO', 'NAO_SE_APLICA'] as const;
export const TIPO_RECEITA = ['VENDA', 'LOCACAO', 'OUTRA'] as const;
export const CODIGO_INDICE = ['IPCA', 'INCC', 'IGPM', 'SELIC'] as const;
export const TIPO_MEMORANDO = ['MEMORANDO', 'ACAO_ESTRATEGICA', 'ACAO_OPERACIONAL'] as const;
export const TIPO_COMPROMISSO = ['TAREFA', 'REUNIAO', 'VISITA'] as const;
export const STATUS_COMPROMISSO = ['PENDENTE', 'CONCLUIDO', 'CANCELADO'] as const;
export const TIPO_ALERTA = ['COMPROMISSO_NAO_CUMPRIDO', 'DESPESA_NAO_PAGA'] as const;
export const FINALIDADE_LEAD = ['COMPRA', 'LOCACAO'] as const;
export const FUNIL = ['VENDA', 'LOCACAO'] as const;
export const TIPO_ESTAGIO = ['ABERTO', 'GANHO', 'PERDIDO'] as const;
export const CANAL_INTERACAO = ['TELEFONE', 'WHATSAPP', 'EMAIL', 'VISITA'] as const;

export type StatusLancamento = (typeof STATUS_LANCAMENTO)[number];
export type SituacaoImovel = (typeof SITUACAO_IMOVEL)[number];

/* ------------------------- RBAC: catálogo de permissões ---------------------- */
export const PERMISSOES = [
  'usuarios:gerir', 'rbac:gerir', 'admin:config', 'auditoria:ver',
  'pessoas:ver', 'pessoas:criar', 'pessoas:editar', 'pessoas:excluir',
  'imoveis:ver', 'imoveis:criar', 'imoveis:editar', 'imoveis:excluir', 'imoveis:exportar',
  'financeiro:ver', 'financeiro:editar', 'financeiro:exportar',
  'memorandos:ver', 'memorandos:criar', 'memorandos:editar', 'memorandos:excluir',
  'agenda:ver', 'agenda:criar', 'agenda:editar', 'agenda:excluir',
  'crm:ver', 'crm:criar', 'crm:editar', 'crm:excluir',
] as const;
export type Permissao = (typeof PERMISSOES)[number];

export const PERFIS_PADRAO: { nome: string; descricao: string; escopo_proprio: boolean; permissoes: readonly Permissao[] }[] = [
  { nome: 'Administrador', descricao: 'Acesso total ao sistema', escopo_proprio: false, permissoes: PERMISSOES },
  {
    nome: 'Corretor', descricao: 'Capta e atende; vê apenas os seus imóveis e leads', escopo_proprio: true,
    permissoes: [
      'pessoas:ver', 'pessoas:criar', 'pessoas:editar',
      'imoveis:ver', 'imoveis:criar', 'imoveis:editar', 'imoveis:exportar',
      'memorandos:ver', 'memorandos:criar', 'memorandos:editar',
      'agenda:ver', 'agenda:criar', 'agenda:editar', 'agenda:excluir',
      'crm:ver', 'crm:criar', 'crm:editar',
    ],
  },
  {
    nome: 'Financeiro', descricao: 'Gerencia aquisição, despesas e retorno dos imóveis', escopo_proprio: false,
    permissoes: [
      'pessoas:ver', 'imoveis:ver', 'imoveis:exportar',
      'financeiro:ver', 'financeiro:editar', 'financeiro:exportar',
      'memorandos:ver', 'memorandos:criar', 'memorandos:editar',
      'agenda:ver', 'agenda:criar', 'agenda:editar',
      'crm:ver',
    ],
  },
];

export const CATEGORIAS_DESPESA_PADRAO = [
  'Reformas', 'Manutenção', 'IPTU', 'Água', 'Luz', 'Condomínio', 'Taxas cartorárias', 'Honorários profissionais', 'Seguro', 'Outras',
] as const;

export const ESTAGIOS_PADRAO: { nome: string; tipo: (typeof TIPO_ESTAGIO)[number]; cor: string }[] = [
  { nome: 'Novo Lead', tipo: 'ABERTO', cor: '#64748b' },
  { nome: 'Contato Realizado', tipo: 'ABERTO', cor: '#0ea5e9' },
  { nome: 'Visita Agendada', tipo: 'ABERTO', cor: '#8b5cf6' },
  { nome: 'Proposta', tipo: 'ABERTO', cor: '#f59e0b' },
  { nome: 'Negociação', tipo: 'ABERTO', cor: '#f97316' },
  { nome: 'Concluído', tipo: 'GANHO', cor: '#16a34a' },
  { nome: 'Perdido', tipo: 'PERDIDO', cor: '#dc2626' },
];

/* ---------------------------- Tipos da API ---------------------------------- */
export interface Paginado<T> { data: T[]; total: number; page: number; pageSize: number }
export interface ProblemDetails { status: number; title: string; detail?: string; errors?: Record<string, string[]> }

export interface UsuarioLogado {
  id: string; nome: string; email: string;
  perfil: { id: string; nome: string; escopo_proprio: boolean };
  permissoes: Permissao[];
}
export interface LoginResposta { access_token: string; expires_in: number; usuario: UsuarioLogado }

/** Regra de negócio: Total = Valor Pago + Impostos + Taxas + Despesas (centavos, sem float). */
export function calcularTotalAquisicao(i: { valor_pago: string | number; impostos: string | number; taxas: string | number; despesas_diversas: string | number }): string {
  const c = (v: string | number) => Math.round(Number(v) * 100);
  return ((c(i.valor_pago) + c(i.impostos) + c(i.taxas) + c(i.despesas_diversas)) / 100).toFixed(2);
}

/** Regra de negócio: NAO_PAGO exige justificativa (>= 10 caracteres úteis). */
export const justificativaSchema = z.string().trim().min(10, 'Informe a justificativa (mínimo 10 caracteres)');
export const atualizarStatusLancamentoSchema = z
  .object({ status: z.enum(STATUS_LANCAMENTO), justificativa: z.string().trim().optional(), data_pagamento: z.string().optional(), valor: z.string().optional() })
  .superRefine((v, ctx) => {
    if (v.status === 'NAO_PAGO' && (v.justificativa ?? '').length < 10) {
      ctx.addIssue({ code: 'custom', path: ['justificativa'], message: 'Justificativa obrigatória (mínimo 10 caracteres) quando o status é Não Pago' });
    }
  });
