import { sql } from 'drizzle-orm';
import {
  boolean, check, date, doublePrecision, index, integer, jsonb, numeric, pgEnum, pgTable,
  primaryKey, text, timestamp, uniqueIndex, uuid, varchar,
} from 'drizzle-orm/pg-core';

/* ------------------------------------------------------------------ *
 * Convenções: colunas em snake_case (iguais aos campos da API),
 * valores monetários em NUMERIC(15,2) (string no TS), uuid como PK,
 * criado_em/atualizado_em e exclusão lógica (excluido_em) nas entidades de negócio.
 * ------------------------------------------------------------------ */

const id = () => uuid('id').primaryKey().default(sql`gen_random_uuid()`);
const money = (name: string) => numeric(name, { precision: 15, scale: 2, mode: 'string' });
const ts = (name: string) => timestamp(name, { withTimezone: true, mode: 'date' });
const audit = () => ({
  criado_em: ts('criado_em').notNull().defaultNow(),
  atualizado_em: ts('atualizado_em').notNull().defaultNow(),
});
const softDelete = () => ({ excluido_em: ts('excluido_em') });

/* ----------------------------- ENUMS ------------------------------ */
export const tipoPessoaEnum = pgEnum('tipo_pessoa', ['PF', 'PJ']);
export const papelPessoaEnum = pgEnum('papel_pessoa', ['CLIENTE', 'PROPRIETARIO', 'FIADOR', 'PARCEIRO', 'PRESTADOR', 'TERCEIRO']);
export const finalidadeImovelEnum = pgEnum('finalidade_imovel', ['VENDA', 'LOCACAO', 'AMBOS']);
export const situacaoImovelEnum = pgEnum('situacao_imovel', [
  'CAPTACAO', 'EM_AQUISICAO', 'EM_REGULARIZACAO', 'EM_RETROFIT', 'DISPONIVEL', 'VENDIDO', 'LOCADO', 'ARQUIVADO',
]);
export const tipoMidiaEnum = pgEnum('tipo_midia', ['FOTO', 'VIDEO', 'PLANTA', 'DOCUMENTO']);
export const tipoRegularizacaoEnum = pgEnum('tipo_regularizacao', [
  'DOCUMENTACAO', 'TRIBUTOS_ATRASO', 'DESMEMBRAMENTO', 'FRACIONAMENTO', 'RETIFICACAO_AREA', 'OUTRO',
]);
export const statusRegularizacaoEnum = pgEnum('status_regularizacao', ['NAO_INICIADA', 'EM_ANDAMENTO', 'CONCLUIDA', 'CANCELADA']);
export const tipoItemAquisicaoEnum = pgEnum('tipo_item_aquisicao', ['IMPOSTO', 'TAXA', 'DESPESA']);
export const recorrenciaEnum = pgEnum('recorrencia', ['UNICA', 'MENSAL', 'ANUAL']);
export const statusLancamentoEnum = pgEnum('status_lancamento', ['PAGO', 'NAO_PAGO', 'NAO_SE_APLICA']);
export const tipoReceitaEnum = pgEnum('tipo_receita', ['VENDA', 'LOCACAO', 'OUTRA']);
export const codigoIndiceEnum = pgEnum('codigo_indice', ['IPCA', 'INCC', 'IGPM', 'SELIC']);
export const tipoMemorandoEnum = pgEnum('tipo_memorando', ['MEMORANDO', 'ACAO_ESTRATEGICA', 'ACAO_OPERACIONAL']);
export const tipoCompromissoEnum = pgEnum('tipo_compromisso', ['TAREFA', 'REUNIAO', 'VISITA']);
export const statusCompromissoEnum = pgEnum('status_compromisso', ['PENDENTE', 'CONCLUIDO', 'CANCELADO']);
export const tipoAlertaEnum = pgEnum('tipo_alerta', ['COMPROMISSO_NAO_CUMPRIDO', 'DESPESA_NAO_PAGA']);
export const finalidadeLeadEnum = pgEnum('finalidade_lead', ['COMPRA', 'LOCACAO']);
export const funilEnum = pgEnum('funil', ['VENDA', 'LOCACAO']);
export const tipoEstagioEnum = pgEnum('tipo_estagio', ['ABERTO', 'GANHO', 'PERDIDO']);
export const canalInteracaoEnum = pgEnum('canal_interacao', ['TELEFONE', 'WHATSAPP', 'EMAIL', 'VISITA']);

/* ------------------- 4.1 ACESSO E AUDITORIA ----------------------- */
export const perfis = pgTable('perfis', {
  id: id(),
  nome: varchar('nome', { length: 60 }).notNull().unique(),
  descricao: text('descricao'),
  /** true = enxerga apenas os registros pelos quais é responsável (ex.: Corretor) */
  escopo_proprio: boolean('escopo_proprio').notNull().default(false),
  ...audit(),
});

export const permissoes = pgTable('permissoes', {
  id: id(),
  codigo: varchar('codigo', { length: 80 }).notNull().unique(), // ex.: financeiro:editar
  descricao: text('descricao'),
});

export const perfil_permissoes = pgTable('perfil_permissoes', {
  perfil_id: uuid('perfil_id').notNull().references(() => perfis.id, { onDelete: 'cascade' }),
  permissao_id: uuid('permissao_id').notNull().references(() => permissoes.id, { onDelete: 'cascade' }),
}, (t) => [primaryKey({ columns: [t.perfil_id, t.permissao_id] })]);

export const usuarios = pgTable('usuarios', {
  id: id(),
  nome: varchar('nome', { length: 160 }).notNull(),
  email: varchar('email', { length: 200 }).notNull(),
  senha_hash: text('senha_hash').notNull(),
  perfil_id: uuid('perfil_id').notNull().references(() => perfis.id),
  ativo: boolean('ativo').notNull().default(true),
  totp_secret: text('totp_secret'),
  totp_ativo: boolean('totp_ativo').notNull().default(false),
  tentativas_falhas: integer('tentativas_falhas').notNull().default(0),
  bloqueado_ate: ts('bloqueado_ate'),
  ultimo_login_em: ts('ultimo_login_em'),
  ...audit(), ...softDelete(),
}, (t) => [uniqueIndex('ux_usuarios_email').on(sql`lower(${t.email})`)]);

export const audit_log = pgTable('audit_log', {
  id: id(),
  usuario_id: uuid('usuario_id').references(() => usuarios.id),
  entidade: varchar('entidade', { length: 80 }).notNull(),
  entidade_id: uuid('entidade_id'),
  acao: varchar('acao', { length: 40 }).notNull(), // CRIAR | ATUALIZAR | EXCLUIR | LOGIN ...
  valores_antes: jsonb('valores_antes'),
  valores_depois: jsonb('valores_depois'),
  ip: varchar('ip', { length: 64 }),
  criado_em: ts('criado_em').notNull().defaultNow(),
}, (t) => [index('ix_audit_entidade').on(t.entidade, t.entidade_id), index('ix_audit_criado').on(t.criado_em)]);

/** Refresh tokens opacos e rotativos (guardamos só o HMAC). `familia_id` permite revogar a cadeia se um token já usado for reapresentado. */
export const refresh_tokens = pgTable('refresh_tokens', {
  id: id(),
  usuario_id: uuid('usuario_id').notNull().references(() => usuarios.id, { onDelete: 'cascade' }),
  familia_id: uuid('familia_id').notNull(),
  token_hash: varchar('token_hash', { length: 64 }).notNull().unique(),
  expira_em: ts('expira_em').notNull(),
  revogado_em: ts('revogado_em'),
  ip: varchar('ip', { length: 64 }),
  criado_em: ts('criado_em').notNull().defaultNow(),
}, (t) => [index('ix_refresh_usuario').on(t.usuario_id), index('ix_refresh_familia').on(t.familia_id)]);

export const senha_resets = pgTable('senha_resets', {
  id: id(),
  usuario_id: uuid('usuario_id').notNull().references(() => usuarios.id, { onDelete: 'cascade' }),
  token_hash: varchar('token_hash', { length: 64 }).notNull().unique(),
  expira_em: ts('expira_em').notNull(),
  usado_em: ts('usado_em'),
  criado_em: ts('criado_em').notNull().defaultNow(),
});

/* ------------------------- 4.2 PESSOAS ---------------------------- */
export const pessoas = pgTable('pessoas', {
  id: id(),
  tipo: tipoPessoaEnum('tipo').notNull().default('PF'),
  nome_razao: varchar('nome_razao', { length: 200 }).notNull(),
  cpf_cnpj: varchar('cpf_cnpj', { length: 20 }),
  rg: varchar('rg', { length: 30 }),
  email: varchar('email', { length: 200 }),
  telefone: varchar('telefone', { length: 30 }),
  whatsapp: varchar('whatsapp', { length: 30 }),
  cep: varchar('cep', { length: 12 }),
  logradouro: varchar('logradouro', { length: 200 }),
  numero: varchar('numero', { length: 20 }),
  complemento: varchar('complemento', { length: 100 }),
  bairro: varchar('bairro', { length: 100 }),
  cidade: varchar('cidade', { length: 100 }),
  uf: varchar('uf', { length: 2 }),
  observacoes: text('observacoes'),
  lgpd_consentimento_em: ts('lgpd_consentimento_em'),
  criado_por: uuid('criado_por').references(() => usuarios.id),
  ...audit(), ...softDelete(),
}, (t) => [
  uniqueIndex('ux_pessoas_cpf_cnpj').on(t.cpf_cnpj).where(sql`${t.cpf_cnpj} IS NOT NULL AND ${t.excluido_em} IS NULL`),
  index('ix_pessoas_nome').on(t.nome_razao),
]);

export const pessoa_papeis = pgTable('pessoa_papeis', {
  pessoa_id: uuid('pessoa_id').notNull().references(() => pessoas.id, { onDelete: 'cascade' }),
  papel: papelPessoaEnum('papel').notNull(),
  especialidade: varchar('especialidade', { length: 100 }), // prestadores: advogado, topógrafo, despachante...
}, (t) => [primaryKey({ columns: [t.pessoa_id, t.papel] })]);

/* ------------------------- 4.3 IMÓVEL ---------------------------- */
export const imoveis = pgTable('imoveis', {
  id: id(),
  codigo: varchar('codigo', { length: 20 }).notNull().unique(), // IMV-000123
  finalidade: finalidadeImovelEnum('finalidade').notNull().default('VENDA'),
  situacao: situacaoImovelEnum('situacao').notNull().default('CAPTACAO'),
  tipo: varchar('tipo', { length: 40 }).notNull(), // apartamento, casa, terreno, sala comercial, galpão...
  subtipo: varchar('subtipo', { length: 60 }),
  titulo: varchar('titulo', { length: 200 }).notNull(),
  descricao_publica: text('descricao_publica'),
  descricao_interna: text('descricao_interna'),
  cep: varchar('cep', { length: 12 }),
  logradouro: varchar('logradouro', { length: 200 }),
  numero: varchar('numero', { length: 20 }),
  complemento: varchar('complemento', { length: 100 }),
  bairro: varchar('bairro', { length: 100 }),
  cidade: varchar('cidade', { length: 100 }),
  uf: varchar('uf', { length: 2 }),
  latitude: doublePrecision('latitude'),
  longitude: doublePrecision('longitude'),
  /** exibir endereço completo no site? (campo público x privado) */
  endereco_publico: boolean('endereco_publico').notNull().default(false),
  matricula: varchar('matricula', { length: 40 }),
  cartorio: varchar('cartorio', { length: 120 }),
  inscricao_municipal: varchar('inscricao_municipal', { length: 40 }),
  area_total_m2: numeric('area_total_m2', { precision: 12, scale: 2, mode: 'string' }),
  area_construida_m2: numeric('area_construida_m2', { precision: 12, scale: 2, mode: 'string' }),
  area_util_m2: numeric('area_util_m2', { precision: 12, scale: 2, mode: 'string' }),
  quartos: integer('quartos'),
  suites: integer('suites'),
  banheiros: integer('banheiros'),
  vagas: integer('vagas'),
  preco_venda: money('preco_venda'),
  preco_locacao: money('preco_locacao'),
  divulgar_site: boolean('divulgar_site').notNull().default(false),
  corretor_responsavel_id: uuid('corretor_responsavel_id').references(() => usuarios.id),
  atributos: jsonb('atributos').$type<Record<string, unknown>>().default({}),
  ...audit(), ...softDelete(),
}, (t) => [
  index('ix_imoveis_situacao').on(t.situacao),
  index('ix_imoveis_corretor').on(t.corretor_responsavel_id),
  index('ix_imoveis_cidade').on(t.cidade, t.bairro),
]);

export const imovel_caracteristicas = pgTable('imovel_caracteristicas', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  chave: varchar('chave', { length: 80 }).notNull(),
  valor: varchar('valor', { length: 200 }),
  publico: boolean('publico').notNull().default(true),
}, (t) => [uniqueIndex('ux_imovel_carac').on(t.imovel_id, t.chave)]);

export const imovel_midias = pgTable('imovel_midias', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  tipo: tipoMidiaEnum('tipo').notNull().default('FOTO'),
  arquivo_chave: text('arquivo_chave').notNull(), // chave no S3
  nome_original: varchar('nome_original', { length: 255 }),
  legenda: varchar('legenda', { length: 200 }),
  ordem: integer('ordem').notNull().default(0),
  capa: boolean('capa').notNull().default(false),
  publica: boolean('publica').notNull().default(true),
  criado_em: ts('criado_em').notNull().defaultNow(),
}, (t) => [index('ix_midias_imovel').on(t.imovel_id, t.ordem)]);

export const imovel_proprietarios = pgTable('imovel_proprietarios', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  pessoa_id: uuid('pessoa_id').notNull().references(() => pessoas.id),
  percentual: numeric('percentual', { precision: 5, scale: 2, mode: 'string' }).notNull().default('100'),
  principal: boolean('principal').notNull().default(false),
  inicio_em: date('inicio_em', { mode: 'string' }),
  fim_em: date('fim_em', { mode: 'string' }),
}, (t) => [check('ck_percentual', sql`${t.percentual} > 0 AND ${t.percentual} <= 100`), index('ix_prop_imovel').on(t.imovel_id)]);

export const imovel_envolvidos = pgTable('imovel_envolvidos', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  pessoa_id: uuid('pessoa_id').notNull().references(() => pessoas.id),
  papel: papelPessoaEnum('papel').notNull(),
  observacao: text('observacao'),
}, (t) => [uniqueIndex('ux_envolvido').on(t.imovel_id, t.pessoa_id, t.papel)]);

export const regularizacoes = pgTable('regularizacoes', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  tipo: tipoRegularizacaoEnum('tipo').notNull(),
  status: statusRegularizacaoEnum('status').notNull().default('NAO_INICIADA'),
  profissional_id: uuid('profissional_id').references(() => pessoas.id),
  prazo_previsto: date('prazo_previsto', { mode: 'string' }),
  concluida_em: date('concluida_em', { mode: 'string' }),
  custo_estimado: money('custo_estimado'),
  descricao: text('descricao'),
  ...audit(), ...softDelete(),
}, (t) => [index('ix_reg_imovel').on(t.imovel_id)]);

/* ------------------------- 4.4 FINANCEIRO ------------------------ */
export const aquisicoes = pgTable('aquisicoes', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().unique().references(() => imoveis.id, { onDelete: 'cascade' }),
  data_aquisicao: date('data_aquisicao', { mode: 'string' }).notNull(),
  valor_pago: money('valor_pago').notNull().default('0'),
  impostos: money('impostos').notNull().default('0'),
  taxas: money('taxas').notNull().default('0'),
  despesas_diversas: money('despesas_diversas').notNull().default('0'),
  /** Coluna gerada: Total = Valor Pago + Impostos + Taxas + Despesas. Ninguém grava. */
  total_aquisicao: numeric('total_aquisicao', { precision: 15, scale: 2, mode: 'string' })
    .generatedAlwaysAs(sql`valor_pago + impostos + taxas + despesas_diversas`),
  forma_pagamento: varchar('forma_pagamento', { length: 80 }),
  observacao: text('observacao'),
  ...audit(),
}, (t) => [check('ck_aquisicao_nao_negativa', sql`${t.valor_pago} >= 0 AND ${t.impostos} >= 0 AND ${t.taxas} >= 0 AND ${t.despesas_diversas} >= 0`)]);

export const aquisicao_itens = pgTable('aquisicao_itens', {
  id: id(),
  aquisicao_id: uuid('aquisicao_id').notNull().references(() => aquisicoes.id, { onDelete: 'cascade' }),
  tipo: tipoItemAquisicaoEnum('tipo').notNull(),
  descricao: varchar('descricao', { length: 200 }).notNull(), // ITBI, escritura, registro, certidões...
  valor: money('valor').notNull(),
  data_pagamento: date('data_pagamento', { mode: 'string' }),
  comprovante_chave: text('comprovante_chave'),
  ...audit(),
}, (t) => [check('ck_item_valor', sql`${t.valor} >= 0`), index('ix_item_aq').on(t.aquisicao_id)]);

export const despesa_categorias = pgTable('despesa_categorias', {
  id: id(),
  nome: varchar('nome', { length: 80 }).notNull().unique(),
  ativo: boolean('ativo').notNull().default(true),
});

export const despesas = pgTable('despesas', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  categoria_id: uuid('categoria_id').notNull().references(() => despesa_categorias.id),
  regularizacao_id: uuid('regularizacao_id').references(() => regularizacoes.id),
  descricao: varchar('descricao', { length: 200 }).notNull(),
  recorrencia: recorrenciaEnum('recorrencia').notNull().default('UNICA'),
  dia_vencimento: integer('dia_vencimento'),
  valor_previsto: money('valor_previsto').notNull().default('0'),
  inicio_em: date('inicio_em', { mode: 'string' }).notNull(),
  fim_em: date('fim_em', { mode: 'string' }),
  ativa: boolean('ativa').notNull().default(true),
  ...audit(), ...softDelete(),
}, (t) => [
  check('ck_dia_venc', sql`${t.dia_vencimento} IS NULL OR (${t.dia_vencimento} BETWEEN 1 AND 31)`),
  index('ix_despesas_imovel').on(t.imovel_id),
]);

export const despesa_lancamentos = pgTable('despesa_lancamentos', {
  id: id(),
  despesa_id: uuid('despesa_id').notNull().references(() => despesas.id, { onDelete: 'cascade' }),
  /** 1º dia do mês de competência */
  competencia: date('competencia', { mode: 'string' }).notNull(),
  vencimento: date('vencimento', { mode: 'string' }),
  valor: money('valor').notNull().default('0'),
  /** NULL = "a marcar" (gerado pelo job, ainda sem decisão) */
  status: statusLancamentoEnum('status'),
  data_pagamento: date('data_pagamento', { mode: 'string' }),
  justificativa: text('justificativa'),
  comprovante_chave: text('comprovante_chave'),
  atualizado_por: uuid('atualizado_por').references(() => usuarios.id),
  ...audit(),
}, (t) => [
  uniqueIndex('ux_lancamento_competencia').on(t.despesa_id, t.competencia),
  // Regra de negócio: NAO_PAGO exige justificativa preenchida (>= 10 caracteres úteis)
  check('ck_justificativa_nao_pago', sql`${t.status} IS DISTINCT FROM 'NAO_PAGO' OR length(btrim(coalesce(${t.justificativa}, ''))) >= 10`),
  check('ck_competencia_dia1', sql`extract(day from ${t.competencia}) = 1`),
  index('ix_lanc_status').on(t.status, t.competencia),
]);

export const receitas = pgTable('receitas', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  tipo: tipoReceitaEnum('tipo').notNull(),
  data: date('data', { mode: 'string' }).notNull(),
  valor_bruto: money('valor_bruto').notNull(),
  custos_venda: money('custos_venda').notNull().default('0'), // corretagem etc.
  tributos: money('tributos').notNull().default('0'),
  valor_liquido: numeric('valor_liquido', { precision: 15, scale: 2, mode: 'string' })
    .generatedAlwaysAs(sql`valor_bruto - custos_venda - tributos`),
  contraparte_id: uuid('contraparte_id').references(() => pessoas.id),
  observacao: text('observacao'),
  ...audit(), ...softDelete(),
}, (t) => [index('ix_receitas_imovel').on(t.imovel_id, t.data)]);

export const indices_serie = pgTable('indices_serie', {
  id: id(),
  codigo: codigoIndiceEnum('codigo').notNull(),
  competencia: date('competencia', { mode: 'string' }).notNull(), // 1º dia do mês
  variacao_mensal_pct: numeric('variacao_mensal_pct', { precision: 12, scale: 6, mode: 'string' }).notNull(),
  fonte: varchar('fonte', { length: 40 }).notNull().default('BCB-SGS'),
  atualizado_em: ts('atualizado_em').notNull().defaultNow(),
}, (t) => [uniqueIndex('ux_indice_comp').on(t.codigo, t.competencia)]);

/* ------------- 4.5 AGENDA, ALERTAS E MEMORANDOS ----------------- */
export const memorandos = pgTable('memorandos', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  autor_id: uuid('autor_id').notNull().references(() => usuarios.id),
  tipo: tipoMemorandoEnum('tipo').notNull().default('MEMORANDO'),
  titulo: varchar('titulo', { length: 200 }).notNull(),
  corpo: text('corpo').notNull().default(''),
  fixado: boolean('fixado').notNull().default(false),
  ...audit(), ...softDelete(),
}, (t) => [index('ix_memo_imovel').on(t.imovel_id, t.criado_em)]);

export const google_contas = pgTable('google_contas', {
  id: id(),
  usuario_id: uuid('usuario_id').notNull().unique().references(() => usuarios.id, { onDelete: 'cascade' }),
  google_email: varchar('google_email', { length: 200 }),
  refresh_token_cifrado: text('refresh_token_cifrado').notNull(),
  calendar_id: varchar('calendar_id', { length: 200 }).notNull().default('primary'),
  sync_token: text('sync_token'),
  canal_watch_id: varchar('canal_watch_id', { length: 100 }),
  canal_resource_id: varchar('canal_resource_id', { length: 200 }),
  canal_expira_em: ts('canal_expira_em'),
  ultima_sync_em: ts('ultima_sync_em'),
  ...audit(),
});

export const leads = pgTable('leads', {
  id: id(),
  pessoa_id: uuid('pessoa_id').notNull().references(() => pessoas.id),
  finalidade: finalidadeLeadEnum('finalidade').notNull().default('COMPRA'),
  origem: varchar('origem', { length: 80 }),
  estagio_id: uuid('estagio_id').notNull().references(() => funil_estagios.id),
  responsavel_id: uuid('responsavel_id').references(() => usuarios.id),
  orcamento_min: money('orcamento_min'),
  orcamento_max: money('orcamento_max'),
  motivo_perda: text('motivo_perda'),
  proxima_acao_em: ts('proxima_acao_em'),
  ordem: integer('ordem').notNull().default(0),
  ...audit(), ...softDelete(),
}, (t) => [index('ix_leads_estagio').on(t.estagio_id), index('ix_leads_resp').on(t.responsavel_id)]);

export const compromissos = pgTable('compromissos', {
  id: id(),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  lead_id: uuid('lead_id').references(() => leads.id),
  tipo: tipoCompromissoEnum('tipo').notNull().default('TAREFA'),
  titulo: varchar('titulo', { length: 200 }).notNull(),
  descricao: text('descricao'),
  local: varchar('local', { length: 200 }),
  inicio_em: ts('inicio_em').notNull(),
  fim_em: ts('fim_em').notNull(),
  status: statusCompromissoEnum('status').notNull().default('PENDENTE'),
  concluido_em: ts('concluido_em'),
  responsavel_id: uuid('responsavel_id').notNull().references(() => usuarios.id),
  google_event_id: varchar('google_event_id', { length: 200 }),
  google_etag: varchar('google_etag', { length: 200 }),
  sincronizado_em: ts('sincronizado_em'),
  ...audit(), ...softDelete(),
}, (t) => [
  check('ck_comp_periodo', sql`${t.fim_em} >= ${t.inicio_em}`),
  // consulta do job de alertas (a cada 5 minutos)
  index('ix_compromissos_vencidos').on(t.fim_em).where(sql`${t.status} = 'PENDENTE'`),
  index('ix_comp_imovel').on(t.imovel_id, t.inicio_em),
  uniqueIndex('ux_comp_google').on(t.responsavel_id, t.google_event_id).where(sql`${t.google_event_id} IS NOT NULL`),
]);

export const compromisso_participantes = pgTable('compromisso_participantes', {
  id: id(),
  compromisso_id: uuid('compromisso_id').notNull().references(() => compromissos.id, { onDelete: 'cascade' }),
  usuario_id: uuid('usuario_id').references(() => usuarios.id),
  pessoa_id: uuid('pessoa_id').references(() => pessoas.id),
  email: varchar('email', { length: 200 }),
  resposta: varchar('resposta', { length: 20 }),
}, (t) => [index('ix_part_comp').on(t.compromisso_id)]);

export const alertas = pgTable('alertas', {
  id: id(),
  tipo: tipoAlertaEnum('tipo').notNull(),
  usuario_id: uuid('usuario_id').notNull().references(() => usuarios.id, { onDelete: 'cascade' }),
  compromisso_id: uuid('compromisso_id').references(() => compromissos.id, { onDelete: 'cascade' }),
  lancamento_id: uuid('lancamento_id').references(() => despesa_lancamentos.id, { onDelete: 'cascade' }),
  mensagem: text('mensagem').notNull(),
  gerado_em: ts('gerado_em').notNull().defaultNow(),
  lido_em: ts('lido_em'),
  resolvido_em: ts('resolvido_em'),
}, (t) => [
  // Um alerta aberto por compromisso/tipo
  uniqueIndex('ux_alerta_aberto').on(t.compromisso_id, t.tipo).where(sql`${t.resolvido_em} IS NULL AND ${t.compromisso_id} IS NOT NULL`),
  // por usuário: cada pessoa com acesso ao financeiro recebe o seu alerta da mesma despesa
  uniqueIndex('ux_alerta_lanc_aberto').on(t.lancamento_id, t.tipo, t.usuario_id).where(sql`${t.resolvido_em} IS NULL AND ${t.lancamento_id} IS NOT NULL`),
  index('ix_alertas_usuario').on(t.usuario_id, t.resolvido_em),
]);

/* ------------------------- 4.6 CRM ------------------------------- */
export const funil_estagios = pgTable('funil_estagios', {
  id: id(),
  funil: funilEnum('funil').notNull().default('VENDA'),
  nome: varchar('nome', { length: 60 }).notNull(),
  ordem: integer('ordem').notNull().default(0),
  cor: varchar('cor', { length: 9 }).default('#64748b'),
  tipo: tipoEstagioEnum('tipo').notNull().default('ABERTO'),
  ativo: boolean('ativo').notNull().default(true),
}, (t) => [uniqueIndex('ux_estagio_nome').on(t.funil, t.nome)]);

export const lead_historico_estagio = pgTable('lead_historico_estagio', {
  id: id(),
  lead_id: uuid('lead_id').notNull().references(() => leads.id, { onDelete: 'cascade' }),
  estagio_anterior_id: uuid('estagio_anterior_id').references(() => funil_estagios.id),
  estagio_novo_id: uuid('estagio_novo_id').notNull().references(() => funil_estagios.id),
  usuario_id: uuid('usuario_id').references(() => usuarios.id),
  criado_em: ts('criado_em').notNull().defaultNow(),
}, (t) => [index('ix_hist_lead').on(t.lead_id, t.criado_em)]);

export const lead_interacoes = pgTable('lead_interacoes', {
  id: id(),
  lead_id: uuid('lead_id').notNull().references(() => leads.id, { onDelete: 'cascade' }),
  usuario_id: uuid('usuario_id').references(() => usuarios.id),
  canal: canalInteracaoEnum('canal').notNull(),
  resumo: text('resumo').notNull(),
  criado_em: ts('criado_em').notNull().defaultNow(),
}, (t) => [index('ix_inter_lead').on(t.lead_id, t.criado_em)]);

export const lead_imoveis_interesse = pgTable('lead_imoveis_interesse', {
  lead_id: uuid('lead_id').notNull().references(() => leads.id, { onDelete: 'cascade' }),
  imovel_id: uuid('imovel_id').notNull().references(() => imoveis.id, { onDelete: 'cascade' }),
  criado_em: ts('criado_em').notNull().defaultNow(),
}, (t) => [primaryKey({ columns: [t.lead_id, t.imovel_id] })]);
