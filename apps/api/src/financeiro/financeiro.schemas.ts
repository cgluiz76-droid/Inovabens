import { z } from 'zod';
import { RECORRENCIA, TIPO_ITEM_AQUISICAO, TIPO_RECEITA } from '@inovabens/shared';
import { blank, dataISO, dinheiroPositivo, mesISO, uuid } from '../common/schemas';

export const aquisicaoSchema = z.object({
  data_aquisicao: dataISO,
  valor_pago: dinheiroPositivo.default('0.00'),
  impostos: dinheiroPositivo.default('0.00'),
  taxas: dinheiroPositivo.default('0.00'),
  despesas_diversas: dinheiroPositivo.default('0.00'),
  forma_pagamento: blank(z.string().trim().max(80)),
  observacao: blank(z.string().trim()),
  // total_aquisicao é derivado no banco: se o cliente enviar, é ignorado (zod remove campos desconhecidos)
});

const itemBase = z.object({
  tipo: z.enum(TIPO_ITEM_AQUISICAO),
  descricao: z.string().trim().min(2).max(200),
  valor: dinheiroPositivo,
  data_pagamento: blank(dataISO),
});
export const itemCriarSchema = itemBase;
export const itemAtualizarSchema = itemBase.partial();

export const categoriaSchema = z.object({ nome: z.string().trim().min(2).max(80), ativo: z.boolean().default(true) });
export const categoriaPatchSchema = categoriaSchema.partial();

const despesaBase = z.object({
  categoria_id: uuid,
  descricao: z.string().trim().min(2).max(200),
  recorrencia: z.enum(RECORRENCIA).default('UNICA'),
  dia_vencimento: blank(z.number().int().min(1).max(31)),
  valor_previsto: dinheiroPositivo.default('0.00'),
  inicio_em: dataISO,
  fim_em: blank(dataISO),
  regularizacao_id: blank(uuid),
  ativa: z.boolean().default(true),
});
export const despesaCriarSchema = despesaBase;
export const despesaAtualizarSchema = despesaBase.partial();

export const checklistQuery = z.object({ mes: mesISO });
export const historicoQuery = z.object({ de: mesISO.optional(), ate: mesISO.optional() });

/** Corpo extra (além do shared) do PATCH de status: `valor` e comprovante opcionais. */
export const valorLancamento = dinheiroPositivo;

const receitaBase = z.object({
  tipo: z.enum(TIPO_RECEITA),
  data: dataISO,
  valor_bruto: dinheiroPositivo,
  custos_venda: dinheiroPositivo.default('0.00'),
  tributos: dinheiroPositivo.default('0.00'),
  contraparte_id: blank(uuid),
  observacao: blank(z.string().trim()),
});
export const receitaCriarSchema = receitaBase;
export const receitaAtualizarSchema = receitaBase.partial();

export const retornoQuery = z.object({
  indice: z.enum(['IPCA', 'INCC', 'IGPM', 'SELIC']).default('IPCA'),
  ate: dataISO.optional(),
});
