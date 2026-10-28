import { z } from 'zod';
import {
  FINALIDADE_IMOVEL, PAPEL_PESSOA, SITUACAO_IMOVEL, STATUS_REGULARIZACAO, TIPO_MIDIA, TIPO_REGULARIZACAO,
} from '@inovabens/shared';
import { blank, dataISO, dinheiroPositivo, medida, uuid } from '../common/schemas';

const txt = (n: number) => blank(z.string().trim().max(n));
const inteiro = blank(z.number().int().min(0).max(999));

const base = z.object({
  finalidade: z.enum(FINALIDADE_IMOVEL).default('VENDA'),
  situacao: z.enum(SITUACAO_IMOVEL).default('CAPTACAO'),
  tipo: z.string().trim().min(2).max(40),
  subtipo: txt(60),
  titulo: z.string().trim().min(3).max(200),
  descricao_publica: blank(z.string().trim()),
  descricao_interna: blank(z.string().trim()),
  cep: txt(12), logradouro: txt(200), numero: txt(20), complemento: txt(100), bairro: txt(100), cidade: txt(100),
  uf: blank(z.string().trim().length(2).transform((v) => v.toUpperCase())),
  latitude: blank(z.number().min(-90).max(90)),
  longitude: blank(z.number().min(-180).max(180)),
  endereco_publico: z.boolean().default(false),
  matricula: txt(40), cartorio: txt(120), inscricao_municipal: txt(40),
  area_total_m2: blank(medida), area_construida_m2: blank(medida), area_util_m2: blank(medida),
  quartos: inteiro, suites: inteiro, banheiros: inteiro, vagas: inteiro,
  preco_venda: blank(dinheiroPositivo), preco_locacao: blank(dinheiroPositivo),
  divulgar_site: z.boolean().default(false),
  corretor_responsavel_id: blank(uuid),
  atributos: blank(z.record(z.unknown())),
});
export const imovelCriar = base;
export const imovelAtualizar = base.partial();

export const caracteristicasSchema = z.array(z.object({
  chave: z.string().trim().min(1).max(80), valor: txt(200), publico: z.boolean().default(true),
})).max(200);

export const proprietariosSchema = z.array(z.object({
  pessoa_id: uuid,
  percentual: z.union([z.string().regex(/^\d+(\.\d{1,2})?$/), z.number()]).transform((v) => Number(v).toFixed(2)),
  principal: z.boolean().default(false),
  inicio_em: blank(dataISO), fim_em: blank(dataISO),
})).max(50);

export const envolvidosSchema = z.array(z.object({
  pessoa_id: uuid, papel: z.enum(PAPEL_PESSOA), observacao: blank(z.string().trim()),
})).max(100);

export const midiaPatchSchema = z.object({
  legenda: txt(200), ordem: z.number().int().min(0).optional(), capa: z.boolean().optional(), publica: z.boolean().optional(),
});
export const midiaUploadSchema = z.object({
  tipo: z.enum(TIPO_MIDIA).default('FOTO'),
  legenda: txt(200),
  // multipart envia "true"/"false" como texto
  publica: z.preprocess((v) => (v === 'false' ? false : v === 'true' ? true : v), z.boolean().default(true)),
});

const regBase = z.object({
  tipo: z.enum(TIPO_REGULARIZACAO),
  status: z.enum(STATUS_REGULARIZACAO).default('NAO_INICIADA'),
  profissional_id: blank(uuid),
  prazo_previsto: blank(dataISO), concluida_em: blank(dataISO),
  custo_estimado: blank(dinheiroPositivo),
  descricao: blank(z.string().trim()),
});
export const regularizacaoCriar = regBase;
export const regularizacaoAtualizar = regBase.partial();

export const listaImoveisSchema = z.object({
  page: z.coerce.number().int().min(1).default(1),
  pageSize: z.coerce.number().int().min(1).max(100).default(20),
  q: z.string().trim().max(100).optional(),
  situacao: z.enum(SITUACAO_IMOVEL).optional(),
  finalidade: z.enum(FINALIDADE_IMOVEL).optional(),
  tipo: z.string().trim().max(40).optional(),
  cidade: z.string().trim().max(100).optional(),
  corretor_id: uuid.optional(),
});
