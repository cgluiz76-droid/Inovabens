CREATE TYPE "public"."canal_interacao" AS ENUM('TELEFONE', 'WHATSAPP', 'EMAIL', 'VISITA');--> statement-breakpoint
CREATE TYPE "public"."codigo_indice" AS ENUM('IPCA', 'INCC', 'IGPM', 'SELIC');--> statement-breakpoint
CREATE TYPE "public"."finalidade_imovel" AS ENUM('VENDA', 'LOCACAO', 'AMBOS');--> statement-breakpoint
CREATE TYPE "public"."finalidade_lead" AS ENUM('COMPRA', 'LOCACAO');--> statement-breakpoint
CREATE TYPE "public"."funil" AS ENUM('VENDA', 'LOCACAO');--> statement-breakpoint
CREATE TYPE "public"."papel_pessoa" AS ENUM('CLIENTE', 'PROPRIETARIO', 'FIADOR', 'PARCEIRO', 'PRESTADOR', 'TERCEIRO');--> statement-breakpoint
CREATE TYPE "public"."recorrencia" AS ENUM('UNICA', 'MENSAL', 'ANUAL');--> statement-breakpoint
CREATE TYPE "public"."situacao_imovel" AS ENUM('CAPTACAO', 'EM_AQUISICAO', 'EM_REGULARIZACAO', 'EM_RETROFIT', 'DISPONIVEL', 'VENDIDO', 'LOCADO', 'ARQUIVADO');--> statement-breakpoint
CREATE TYPE "public"."status_compromisso" AS ENUM('PENDENTE', 'CONCLUIDO', 'CANCELADO');--> statement-breakpoint
CREATE TYPE "public"."status_lancamento" AS ENUM('PAGO', 'NAO_PAGO', 'NAO_SE_APLICA');--> statement-breakpoint
CREATE TYPE "public"."status_regularizacao" AS ENUM('NAO_INICIADA', 'EM_ANDAMENTO', 'CONCLUIDA', 'CANCELADA');--> statement-breakpoint
CREATE TYPE "public"."tipo_alerta" AS ENUM('COMPROMISSO_NAO_CUMPRIDO', 'DESPESA_NAO_PAGA');--> statement-breakpoint
CREATE TYPE "public"."tipo_compromisso" AS ENUM('TAREFA', 'REUNIAO', 'VISITA');--> statement-breakpoint
CREATE TYPE "public"."tipo_estagio" AS ENUM('ABERTO', 'GANHO', 'PERDIDO');--> statement-breakpoint
CREATE TYPE "public"."tipo_item_aquisicao" AS ENUM('IMPOSTO', 'TAXA', 'DESPESA');--> statement-breakpoint
CREATE TYPE "public"."tipo_memorando" AS ENUM('MEMORANDO', 'ACAO_ESTRATEGICA', 'ACAO_OPERACIONAL');--> statement-breakpoint
CREATE TYPE "public"."tipo_midia" AS ENUM('FOTO', 'VIDEO', 'PLANTA', 'DOCUMENTO');--> statement-breakpoint
CREATE TYPE "public"."tipo_pessoa" AS ENUM('PF', 'PJ');--> statement-breakpoint
CREATE TYPE "public"."tipo_receita" AS ENUM('VENDA', 'LOCACAO', 'OUTRA');--> statement-breakpoint
CREATE TYPE "public"."tipo_regularizacao" AS ENUM('DOCUMENTACAO', 'TRIBUTOS_ATRASO', 'DESMEMBRAMENTO', 'FRACIONAMENTO', 'RETIFICACAO_AREA', 'OUTRO');--> statement-breakpoint
CREATE TABLE "alertas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"tipo" "tipo_alerta" NOT NULL,
	"usuario_id" uuid NOT NULL,
	"compromisso_id" uuid,
	"lancamento_id" uuid,
	"mensagem" text NOT NULL,
	"gerado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"lido_em" timestamp with time zone,
	"resolvido_em" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "aquisicao_itens" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"aquisicao_id" uuid NOT NULL,
	"tipo" "tipo_item_aquisicao" NOT NULL,
	"descricao" varchar(200) NOT NULL,
	"valor" numeric(15, 2) NOT NULL,
	"data_pagamento" date,
	"comprovante_chave" text,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ck_item_valor" CHECK ("aquisicao_itens"."valor" >= 0)
);
--> statement-breakpoint
CREATE TABLE "aquisicoes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"data_aquisicao" date NOT NULL,
	"valor_pago" numeric(15, 2) DEFAULT '0' NOT NULL,
	"impostos" numeric(15, 2) DEFAULT '0' NOT NULL,
	"taxas" numeric(15, 2) DEFAULT '0' NOT NULL,
	"despesas_diversas" numeric(15, 2) DEFAULT '0' NOT NULL,
	"total_aquisicao" numeric(15, 2) GENERATED ALWAYS AS (valor_pago + impostos + taxas + despesas_diversas) STORED,
	"forma_pagamento" varchar(80),
	"observacao" text,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "aquisicoes_imovel_id_unique" UNIQUE("imovel_id"),
	CONSTRAINT "ck_aquisicao_nao_negativa" CHECK ("aquisicoes"."valor_pago" >= 0 AND "aquisicoes"."impostos" >= 0 AND "aquisicoes"."taxas" >= 0 AND "aquisicoes"."despesas_diversas" >= 0)
);
--> statement-breakpoint
CREATE TABLE "audit_log" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"usuario_id" uuid,
	"entidade" varchar(80) NOT NULL,
	"entidade_id" uuid,
	"acao" varchar(40) NOT NULL,
	"valores_antes" jsonb,
	"valores_depois" jsonb,
	"ip" varchar(64),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "compromisso_participantes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"compromisso_id" uuid NOT NULL,
	"usuario_id" uuid,
	"pessoa_id" uuid,
	"email" varchar(200),
	"resposta" varchar(20)
);
--> statement-breakpoint
CREATE TABLE "compromissos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"lead_id" uuid,
	"tipo" "tipo_compromisso" DEFAULT 'TAREFA' NOT NULL,
	"titulo" varchar(200) NOT NULL,
	"descricao" text,
	"local" varchar(200),
	"inicio_em" timestamp with time zone NOT NULL,
	"fim_em" timestamp with time zone NOT NULL,
	"status" "status_compromisso" DEFAULT 'PENDENTE' NOT NULL,
	"concluido_em" timestamp with time zone,
	"responsavel_id" uuid NOT NULL,
	"google_event_id" varchar(200),
	"google_etag" varchar(200),
	"sincronizado_em" timestamp with time zone,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone,
	CONSTRAINT "ck_comp_periodo" CHECK ("compromissos"."fim_em" >= "compromissos"."inicio_em")
);
--> statement-breakpoint
CREATE TABLE "despesa_categorias" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(80) NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL,
	CONSTRAINT "despesa_categorias_nome_unique" UNIQUE("nome")
);
--> statement-breakpoint
CREATE TABLE "despesa_lancamentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"despesa_id" uuid NOT NULL,
	"competencia" date NOT NULL,
	"vencimento" date,
	"valor" numeric(15, 2) DEFAULT '0' NOT NULL,
	"status" "status_lancamento",
	"data_pagamento" date,
	"justificativa" text,
	"comprovante_chave" text,
	"atualizado_por" uuid,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ck_justificativa_nao_pago" CHECK ("despesa_lancamentos"."status" IS DISTINCT FROM 'NAO_PAGO' OR length(btrim(coalesce("despesa_lancamentos"."justificativa", ''))) >= 10),
	CONSTRAINT "ck_competencia_dia1" CHECK (extract(day from "despesa_lancamentos"."competencia") = 1)
);
--> statement-breakpoint
CREATE TABLE "despesas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"categoria_id" uuid NOT NULL,
	"regularizacao_id" uuid,
	"descricao" varchar(200) NOT NULL,
	"recorrencia" "recorrencia" DEFAULT 'UNICA' NOT NULL,
	"dia_vencimento" integer,
	"valor_previsto" numeric(15, 2) DEFAULT '0' NOT NULL,
	"inicio_em" date NOT NULL,
	"fim_em" date,
	"ativa" boolean DEFAULT true NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone,
	CONSTRAINT "ck_dia_venc" CHECK ("despesas"."dia_vencimento" IS NULL OR ("despesas"."dia_vencimento" BETWEEN 1 AND 31))
);
--> statement-breakpoint
CREATE TABLE "funil_estagios" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"funil" "funil" DEFAULT 'VENDA' NOT NULL,
	"nome" varchar(60) NOT NULL,
	"ordem" integer DEFAULT 0 NOT NULL,
	"cor" varchar(9) DEFAULT '#64748b',
	"tipo" "tipo_estagio" DEFAULT 'ABERTO' NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL
);
--> statement-breakpoint
CREATE TABLE "google_contas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"usuario_id" uuid NOT NULL,
	"google_email" varchar(200),
	"refresh_token_cifrado" text NOT NULL,
	"calendar_id" varchar(200) DEFAULT 'primary' NOT NULL,
	"sync_token" text,
	"canal_watch_id" varchar(100),
	"canal_resource_id" varchar(200),
	"canal_expira_em" timestamp with time zone,
	"ultima_sync_em" timestamp with time zone,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "google_contas_usuario_id_unique" UNIQUE("usuario_id")
);
--> statement-breakpoint
CREATE TABLE "imoveis" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"codigo" varchar(20) NOT NULL,
	"finalidade" "finalidade_imovel" DEFAULT 'VENDA' NOT NULL,
	"situacao" "situacao_imovel" DEFAULT 'CAPTACAO' NOT NULL,
	"tipo" varchar(40) NOT NULL,
	"subtipo" varchar(60),
	"titulo" varchar(200) NOT NULL,
	"descricao_publica" text,
	"descricao_interna" text,
	"cep" varchar(12),
	"logradouro" varchar(200),
	"numero" varchar(20),
	"complemento" varchar(100),
	"bairro" varchar(100),
	"cidade" varchar(100),
	"uf" varchar(2),
	"latitude" double precision,
	"longitude" double precision,
	"endereco_publico" boolean DEFAULT false NOT NULL,
	"matricula" varchar(40),
	"cartorio" varchar(120),
	"inscricao_municipal" varchar(40),
	"area_total_m2" numeric(12, 2),
	"area_construida_m2" numeric(12, 2),
	"area_util_m2" numeric(12, 2),
	"quartos" integer,
	"suites" integer,
	"banheiros" integer,
	"vagas" integer,
	"preco_venda" numeric(15, 2),
	"preco_locacao" numeric(15, 2),
	"divulgar_site" boolean DEFAULT false NOT NULL,
	"corretor_responsavel_id" uuid,
	"atributos" jsonb DEFAULT '{}'::jsonb,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone,
	CONSTRAINT "imoveis_codigo_unique" UNIQUE("codigo")
);
--> statement-breakpoint
CREATE TABLE "imovel_caracteristicas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"chave" varchar(80) NOT NULL,
	"valor" varchar(200),
	"publico" boolean DEFAULT true NOT NULL
);
--> statement-breakpoint
CREATE TABLE "imovel_envolvidos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"pessoa_id" uuid NOT NULL,
	"papel" "papel_pessoa" NOT NULL,
	"observacao" text
);
--> statement-breakpoint
CREATE TABLE "imovel_midias" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"tipo" "tipo_midia" DEFAULT 'FOTO' NOT NULL,
	"arquivo_chave" text NOT NULL,
	"nome_original" varchar(255),
	"legenda" varchar(200),
	"ordem" integer DEFAULT 0 NOT NULL,
	"capa" boolean DEFAULT false NOT NULL,
	"publica" boolean DEFAULT true NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "imovel_proprietarios" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"pessoa_id" uuid NOT NULL,
	"percentual" numeric(5, 2) DEFAULT '100' NOT NULL,
	"principal" boolean DEFAULT false NOT NULL,
	"inicio_em" date,
	"fim_em" date,
	CONSTRAINT "ck_percentual" CHECK ("imovel_proprietarios"."percentual" > 0 AND "imovel_proprietarios"."percentual" <= 100)
);
--> statement-breakpoint
CREATE TABLE "indices_serie" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"codigo" "codigo_indice" NOT NULL,
	"competencia" date NOT NULL,
	"variacao_mensal_pct" numeric(12, 6) NOT NULL,
	"fonte" varchar(40) DEFAULT 'BCB-SGS' NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "lead_historico_estagio" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"lead_id" uuid NOT NULL,
	"estagio_anterior_id" uuid,
	"estagio_novo_id" uuid NOT NULL,
	"usuario_id" uuid,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "lead_imoveis_interesse" (
	"lead_id" uuid NOT NULL,
	"imovel_id" uuid NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "lead_imoveis_interesse_lead_id_imovel_id_pk" PRIMARY KEY("lead_id","imovel_id")
);
--> statement-breakpoint
CREATE TABLE "lead_interacoes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"lead_id" uuid NOT NULL,
	"usuario_id" uuid,
	"canal" "canal_interacao" NOT NULL,
	"resumo" text NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE "leads" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"pessoa_id" uuid NOT NULL,
	"finalidade" "finalidade_lead" DEFAULT 'COMPRA' NOT NULL,
	"origem" varchar(80),
	"estagio_id" uuid NOT NULL,
	"responsavel_id" uuid,
	"orcamento_min" numeric(15, 2),
	"orcamento_max" numeric(15, 2),
	"motivo_perda" text,
	"proxima_acao_em" timestamp with time zone,
	"ordem" integer DEFAULT 0 NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "memorandos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"autor_id" uuid NOT NULL,
	"tipo" "tipo_memorando" DEFAULT 'MEMORANDO' NOT NULL,
	"titulo" varchar(200) NOT NULL,
	"corpo" text DEFAULT '' NOT NULL,
	"fixado" boolean DEFAULT false NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "perfil_permissoes" (
	"perfil_id" uuid NOT NULL,
	"permissao_id" uuid NOT NULL,
	CONSTRAINT "perfil_permissoes_perfil_id_permissao_id_pk" PRIMARY KEY("perfil_id","permissao_id")
);
--> statement-breakpoint
CREATE TABLE "perfis" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(60) NOT NULL,
	"descricao" text,
	"escopo_proprio" boolean DEFAULT false NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "perfis_nome_unique" UNIQUE("nome")
);
--> statement-breakpoint
CREATE TABLE "permissoes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"codigo" varchar(80) NOT NULL,
	"descricao" text,
	CONSTRAINT "permissoes_codigo_unique" UNIQUE("codigo")
);
--> statement-breakpoint
CREATE TABLE "pessoa_papeis" (
	"pessoa_id" uuid NOT NULL,
	"papel" "papel_pessoa" NOT NULL,
	"especialidade" varchar(100),
	CONSTRAINT "pessoa_papeis_pessoa_id_papel_pk" PRIMARY KEY("pessoa_id","papel")
);
--> statement-breakpoint
CREATE TABLE "pessoas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"tipo" "tipo_pessoa" DEFAULT 'PF' NOT NULL,
	"nome_razao" varchar(200) NOT NULL,
	"cpf_cnpj" varchar(20),
	"rg" varchar(30),
	"email" varchar(200),
	"telefone" varchar(30),
	"whatsapp" varchar(30),
	"cep" varchar(12),
	"logradouro" varchar(200),
	"numero" varchar(20),
	"complemento" varchar(100),
	"bairro" varchar(100),
	"cidade" varchar(100),
	"uf" varchar(2),
	"observacoes" text,
	"lgpd_consentimento_em" timestamp with time zone,
	"criado_por" uuid,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "receitas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"tipo" "tipo_receita" NOT NULL,
	"data" date NOT NULL,
	"valor_bruto" numeric(15, 2) NOT NULL,
	"custos_venda" numeric(15, 2) DEFAULT '0' NOT NULL,
	"tributos" numeric(15, 2) DEFAULT '0' NOT NULL,
	"valor_liquido" numeric(15, 2) GENERATED ALWAYS AS (valor_bruto - custos_venda - tributos) STORED,
	"contraparte_id" uuid,
	"observacao" text,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "regularizacoes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL,
	"tipo" "tipo_regularizacao" NOT NULL,
	"status" "status_regularizacao" DEFAULT 'NAO_INICIADA' NOT NULL,
	"profissional_id" uuid,
	"prazo_previsto" date,
	"concluida_em" date,
	"custo_estimado" numeric(15, 2),
	"descricao" text,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE "usuarios" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(160) NOT NULL,
	"email" varchar(200) NOT NULL,
	"senha_hash" text NOT NULL,
	"perfil_id" uuid NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL,
	"totp_secret" text,
	"totp_ativo" boolean DEFAULT false NOT NULL,
	"tentativas_falhas" integer DEFAULT 0 NOT NULL,
	"bloqueado_ate" timestamp with time zone,
	"ultimo_login_em" timestamp with time zone,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
--> statement-breakpoint
ALTER TABLE "alertas" ADD CONSTRAINT "alertas_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "alertas" ADD CONSTRAINT "alertas_compromisso_id_compromissos_id_fk" FOREIGN KEY ("compromisso_id") REFERENCES "public"."compromissos"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "alertas" ADD CONSTRAINT "alertas_lancamento_id_despesa_lancamentos_id_fk" FOREIGN KEY ("lancamento_id") REFERENCES "public"."despesa_lancamentos"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "aquisicao_itens" ADD CONSTRAINT "aquisicao_itens_aquisicao_id_aquisicoes_id_fk" FOREIGN KEY ("aquisicao_id") REFERENCES "public"."aquisicoes"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "aquisicoes" ADD CONSTRAINT "aquisicoes_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "audit_log" ADD CONSTRAINT "audit_log_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "compromisso_participantes" ADD CONSTRAINT "compromisso_participantes_compromisso_id_compromissos_id_fk" FOREIGN KEY ("compromisso_id") REFERENCES "public"."compromissos"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "compromisso_participantes" ADD CONSTRAINT "compromisso_participantes_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "compromisso_participantes" ADD CONSTRAINT "compromisso_participantes_pessoa_id_pessoas_id_fk" FOREIGN KEY ("pessoa_id") REFERENCES "public"."pessoas"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "compromissos" ADD CONSTRAINT "compromissos_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "compromissos" ADD CONSTRAINT "compromissos_lead_id_leads_id_fk" FOREIGN KEY ("lead_id") REFERENCES "public"."leads"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "compromissos" ADD CONSTRAINT "compromissos_responsavel_id_usuarios_id_fk" FOREIGN KEY ("responsavel_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "despesa_lancamentos" ADD CONSTRAINT "despesa_lancamentos_despesa_id_despesas_id_fk" FOREIGN KEY ("despesa_id") REFERENCES "public"."despesas"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "despesa_lancamentos" ADD CONSTRAINT "despesa_lancamentos_atualizado_por_usuarios_id_fk" FOREIGN KEY ("atualizado_por") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "despesas" ADD CONSTRAINT "despesas_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "despesas" ADD CONSTRAINT "despesas_categoria_id_despesa_categorias_id_fk" FOREIGN KEY ("categoria_id") REFERENCES "public"."despesa_categorias"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "despesas" ADD CONSTRAINT "despesas_regularizacao_id_regularizacoes_id_fk" FOREIGN KEY ("regularizacao_id") REFERENCES "public"."regularizacoes"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "google_contas" ADD CONSTRAINT "google_contas_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imoveis" ADD CONSTRAINT "imoveis_corretor_responsavel_id_usuarios_id_fk" FOREIGN KEY ("corretor_responsavel_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imovel_caracteristicas" ADD CONSTRAINT "imovel_caracteristicas_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imovel_envolvidos" ADD CONSTRAINT "imovel_envolvidos_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imovel_envolvidos" ADD CONSTRAINT "imovel_envolvidos_pessoa_id_pessoas_id_fk" FOREIGN KEY ("pessoa_id") REFERENCES "public"."pessoas"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imovel_midias" ADD CONSTRAINT "imovel_midias_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imovel_proprietarios" ADD CONSTRAINT "imovel_proprietarios_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "imovel_proprietarios" ADD CONSTRAINT "imovel_proprietarios_pessoa_id_pessoas_id_fk" FOREIGN KEY ("pessoa_id") REFERENCES "public"."pessoas"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_historico_estagio" ADD CONSTRAINT "lead_historico_estagio_lead_id_leads_id_fk" FOREIGN KEY ("lead_id") REFERENCES "public"."leads"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_historico_estagio" ADD CONSTRAINT "lead_historico_estagio_estagio_anterior_id_funil_estagios_id_fk" FOREIGN KEY ("estagio_anterior_id") REFERENCES "public"."funil_estagios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_historico_estagio" ADD CONSTRAINT "lead_historico_estagio_estagio_novo_id_funil_estagios_id_fk" FOREIGN KEY ("estagio_novo_id") REFERENCES "public"."funil_estagios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_historico_estagio" ADD CONSTRAINT "lead_historico_estagio_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_imoveis_interesse" ADD CONSTRAINT "lead_imoveis_interesse_lead_id_leads_id_fk" FOREIGN KEY ("lead_id") REFERENCES "public"."leads"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_imoveis_interesse" ADD CONSTRAINT "lead_imoveis_interesse_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_interacoes" ADD CONSTRAINT "lead_interacoes_lead_id_leads_id_fk" FOREIGN KEY ("lead_id") REFERENCES "public"."leads"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "lead_interacoes" ADD CONSTRAINT "lead_interacoes_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "leads" ADD CONSTRAINT "leads_pessoa_id_pessoas_id_fk" FOREIGN KEY ("pessoa_id") REFERENCES "public"."pessoas"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "leads" ADD CONSTRAINT "leads_estagio_id_funil_estagios_id_fk" FOREIGN KEY ("estagio_id") REFERENCES "public"."funil_estagios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "leads" ADD CONSTRAINT "leads_responsavel_id_usuarios_id_fk" FOREIGN KEY ("responsavel_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "memorandos" ADD CONSTRAINT "memorandos_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "memorandos" ADD CONSTRAINT "memorandos_autor_id_usuarios_id_fk" FOREIGN KEY ("autor_id") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "perfil_permissoes" ADD CONSTRAINT "perfil_permissoes_perfil_id_perfis_id_fk" FOREIGN KEY ("perfil_id") REFERENCES "public"."perfis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "perfil_permissoes" ADD CONSTRAINT "perfil_permissoes_permissao_id_permissoes_id_fk" FOREIGN KEY ("permissao_id") REFERENCES "public"."permissoes"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "pessoa_papeis" ADD CONSTRAINT "pessoa_papeis_pessoa_id_pessoas_id_fk" FOREIGN KEY ("pessoa_id") REFERENCES "public"."pessoas"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "pessoas" ADD CONSTRAINT "pessoas_criado_por_usuarios_id_fk" FOREIGN KEY ("criado_por") REFERENCES "public"."usuarios"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "receitas" ADD CONSTRAINT "receitas_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "receitas" ADD CONSTRAINT "receitas_contraparte_id_pessoas_id_fk" FOREIGN KEY ("contraparte_id") REFERENCES "public"."pessoas"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "regularizacoes" ADD CONSTRAINT "regularizacoes_imovel_id_imoveis_id_fk" FOREIGN KEY ("imovel_id") REFERENCES "public"."imoveis"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "regularizacoes" ADD CONSTRAINT "regularizacoes_profissional_id_pessoas_id_fk" FOREIGN KEY ("profissional_id") REFERENCES "public"."pessoas"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "usuarios" ADD CONSTRAINT "usuarios_perfil_id_perfis_id_fk" FOREIGN KEY ("perfil_id") REFERENCES "public"."perfis"("id") ON DELETE no action ON UPDATE no action;--> statement-breakpoint
CREATE UNIQUE INDEX "ux_alerta_aberto" ON "alertas" USING btree ("compromisso_id","tipo") WHERE "alertas"."resolvido_em" IS NULL AND "alertas"."compromisso_id" IS NOT NULL;--> statement-breakpoint
CREATE UNIQUE INDEX "ux_alerta_lanc_aberto" ON "alertas" USING btree ("lancamento_id","tipo") WHERE "alertas"."resolvido_em" IS NULL AND "alertas"."lancamento_id" IS NOT NULL;--> statement-breakpoint
CREATE INDEX "ix_alertas_usuario" ON "alertas" USING btree ("usuario_id","resolvido_em");--> statement-breakpoint
CREATE INDEX "ix_item_aq" ON "aquisicao_itens" USING btree ("aquisicao_id");--> statement-breakpoint
CREATE INDEX "ix_audit_entidade" ON "audit_log" USING btree ("entidade","entidade_id");--> statement-breakpoint
CREATE INDEX "ix_audit_criado" ON "audit_log" USING btree ("criado_em");--> statement-breakpoint
CREATE INDEX "ix_part_comp" ON "compromisso_participantes" USING btree ("compromisso_id");--> statement-breakpoint
CREATE INDEX "ix_compromissos_vencidos" ON "compromissos" USING btree ("fim_em") WHERE "compromissos"."status" = 'PENDENTE';--> statement-breakpoint
CREATE INDEX "ix_comp_imovel" ON "compromissos" USING btree ("imovel_id","inicio_em");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_comp_google" ON "compromissos" USING btree ("responsavel_id","google_event_id") WHERE "compromissos"."google_event_id" IS NOT NULL;--> statement-breakpoint
CREATE UNIQUE INDEX "ux_lancamento_competencia" ON "despesa_lancamentos" USING btree ("despesa_id","competencia");--> statement-breakpoint
CREATE INDEX "ix_lanc_status" ON "despesa_lancamentos" USING btree ("status","competencia");--> statement-breakpoint
CREATE INDEX "ix_despesas_imovel" ON "despesas" USING btree ("imovel_id");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_estagio_nome" ON "funil_estagios" USING btree ("funil","nome");--> statement-breakpoint
CREATE INDEX "ix_imoveis_situacao" ON "imoveis" USING btree ("situacao");--> statement-breakpoint
CREATE INDEX "ix_imoveis_corretor" ON "imoveis" USING btree ("corretor_responsavel_id");--> statement-breakpoint
CREATE INDEX "ix_imoveis_cidade" ON "imoveis" USING btree ("cidade","bairro");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_imovel_carac" ON "imovel_caracteristicas" USING btree ("imovel_id","chave");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_envolvido" ON "imovel_envolvidos" USING btree ("imovel_id","pessoa_id","papel");--> statement-breakpoint
CREATE INDEX "ix_midias_imovel" ON "imovel_midias" USING btree ("imovel_id","ordem");--> statement-breakpoint
CREATE INDEX "ix_prop_imovel" ON "imovel_proprietarios" USING btree ("imovel_id");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_indice_comp" ON "indices_serie" USING btree ("codigo","competencia");--> statement-breakpoint
CREATE INDEX "ix_hist_lead" ON "lead_historico_estagio" USING btree ("lead_id","criado_em");--> statement-breakpoint
CREATE INDEX "ix_inter_lead" ON "lead_interacoes" USING btree ("lead_id","criado_em");--> statement-breakpoint
CREATE INDEX "ix_leads_estagio" ON "leads" USING btree ("estagio_id");--> statement-breakpoint
CREATE INDEX "ix_leads_resp" ON "leads" USING btree ("responsavel_id");--> statement-breakpoint
CREATE INDEX "ix_memo_imovel" ON "memorandos" USING btree ("imovel_id","criado_em");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_pessoas_cpf_cnpj" ON "pessoas" USING btree ("cpf_cnpj") WHERE "pessoas"."cpf_cnpj" IS NOT NULL AND "pessoas"."excluido_em" IS NULL;--> statement-breakpoint
CREATE INDEX "ix_pessoas_nome" ON "pessoas" USING btree ("nome_razao");--> statement-breakpoint
CREATE INDEX "ix_receitas_imovel" ON "receitas" USING btree ("imovel_id","data");--> statement-breakpoint
CREATE INDEX "ix_reg_imovel" ON "regularizacoes" USING btree ("imovel_id");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_usuarios_email" ON "usuarios" USING btree (lower("email"));