-- Locação, fase 5 (docs/plano-locacao.md): manutenção com chamado, orçamentos de prestadores, aprovação do proprietário
-- por link, agendamento (agenda e chaves), conclusão com custo (repasse, fatura, imóvel ou imobiliária) e preventivas.

-- contrato: até este valor o conserto por conta do locador é aprovado sem consultar o proprietário;
-- token do link que o inquilino usa para abrir chamado sem login
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "limite_manutencao" numeric(14, 2) DEFAULT 0 NOT NULL;
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "token_chamado" varchar(64) UNIQUE;

-- manutenção recorrente do imóvel (caixa d'água, dedetização, AVCB, ar-condicionado): a rotina abre o chamado
CREATE TABLE "manutencoes_preventivas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"titulo" varchar(200) NOT NULL,
	"categoria" varchar(20) NOT NULL,
	"responsavel" varchar(20) NOT NULL CHECK ("responsavel" IN ('LOCADOR', 'LOCATARIO', 'IMOBILIARIA')),
	"periodicidade_meses" smallint NOT NULL CHECK ("periodicidade_meses" BETWEEN 1 AND 120),
	-- data do próximo serviço; o chamado abre 30 dias antes
	"proxima_em" date NOT NULL,
	"prestador_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"observacao" varchar(1000),
	"ativa" boolean DEFAULT true NOT NULL,
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE SEQUENCE IF NOT EXISTS "chamados_manutencao_numero_seq";

CREATE TABLE "chamados_manutencao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"numero" integer NOT NULL DEFAULT nextval('chamados_manutencao_numero_seq') UNIQUE,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"contrato_id" uuid REFERENCES "contratos_locacao"("id") ON DELETE SET NULL,
	"preventiva_id" uuid REFERENCES "manutencoes_preventivas"("id") ON DELETE SET NULL,
	"origem" varchar(20) NOT NULL CHECK ("origem" IN ('EQUIPE', 'INQUILINO', 'PREVENTIVA')),
	"titulo" varchar(200) NOT NULL,
	"descricao" varchar(4000),
	"categoria" varchar(20) NOT NULL,
	"urgencia" varchar(20) NOT NULL CHECK ("urgencia" IN ('BAIXA', 'MEDIA', 'ALTA', 'EMERGENCIA')),
	-- quem arca com o conserto (sugestão pela lei 8.245, arts. 22 e 23; a equipe confirma)
	"responsavel" varchar(20) NOT NULL CHECK ("responsavel" IN ('LOCADOR', 'LOCATARIO', 'IMOBILIARIA')),
	"status" varchar(30) NOT NULL CHECK ("status" IN ('ABERTO', 'ORCAMENTO', 'AGUARDANDO_APROVACAO', 'APROVADO', 'AGENDADO',
		'CONCLUIDO', 'RECUSADO', 'CANCELADO')),
	-- quem pediu (inquilino pelo link: nome e telefone digitados)
	"solicitante_nome" varchar(200),
	"solicitante_telefone" varchar(30),
	"prestador_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"orcamento_id" uuid,
	"valor" numeric(14, 2),
	-- aprovação do proprietário pelo link (sem login)
	"aprovacao_token" varchar(64) UNIQUE,
	"aprovacao_pedida_em" timestamp with time zone,
	"aprovado_em" timestamp with time zone,
	"aprovado_por" varchar(200),
	"aprovacao_obs" varchar(1000),
	"agendado_para" timestamp with time zone,
	"compromisso_id" uuid REFERENCES "compromissos"("id") ON DELETE SET NULL,
	-- conclusão e custo
	"concluido_em" date,
	"valor_final" numeric(14, 2),
	"cobranca" varchar(20) CHECK ("cobranca" IS NULL OR "cobranca" IN ('REPASSE', 'FATURA', 'IMOVEL', 'IMOBILIARIA', 'DIRETO')),
	"vencimento_prestador" date,
	"lancamento_empresa_id" uuid REFERENCES "lancamentos_empresa"("id") ON DELETE SET NULL,
	"lancamento_imovel_id" uuid REFERENCES "lancamentos_financeiros"("id") ON DELETE SET NULL,
	"fatura_item_id" uuid REFERENCES "fatura_locacao_itens"("id") ON DELETE SET NULL,
	"avaliacao" smallint CHECK ("avaliacao" IS NULL OR "avaliacao" BETWEEN 1 AND 5),
	"motivo" varchar(1000),
	"drive_pasta_id" varchar(200),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS "ix_chamados_manutencao_status" ON "chamados_manutencao" ("status", "criado_em");
CREATE INDEX IF NOT EXISTS "ix_chamados_manutencao_contrato" ON "chamados_manutencao" ("contrato_id");

CREATE TABLE "orcamentos_manutencao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"chamado_id" uuid NOT NULL REFERENCES "chamados_manutencao"("id") ON DELETE CASCADE,
	"prestador_id" uuid NOT NULL REFERENCES "pessoas"("id"),
	"valor" numeric(14, 2) NOT NULL CHECK ("valor" >= 0),
	"prazo_dias" smallint,
	"descricao" varchar(2000),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

-- fotos, orçamentos e notas guardados na pasta do chamado no Drive
CREATE TABLE "chamado_manutencao_arquivos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"chamado_id" uuid NOT NULL REFERENCES "chamados_manutencao"("id") ON DELETE CASCADE,
	"nome" varchar(300) NOT NULL,
	"drive_id" varchar(200) NOT NULL,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('FOTO', 'ORCAMENTO', 'NOTA', 'OUTRO')),
	"pelo_inquilino" boolean DEFAULT false NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

-- linha do tempo do chamado (aberto, orçamento, aprovação, agenda, conclusão)
CREATE TABLE "chamado_manutencao_eventos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"chamado_id" uuid NOT NULL REFERENCES "chamados_manutencao"("id") ON DELETE CASCADE,
	"descricao" varchar(2000) NOT NULL,
	"usuario_id" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS "ix_chamado_manutencao_eventos" ON "chamado_manutencao_eventos" ("chamado_id", "criado_em");

-- conserto por conta do locador: descontado dos repasses seguintes (um registro por recebimento que abateu)
CREATE TABLE "manutencao_abatimentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"chamado_id" uuid NOT NULL REFERENCES "chamados_manutencao"("id") ON DELETE CASCADE,
	"pagamento_id" uuid NOT NULL REFERENCES "fatura_locacao_pagamentos"("id") ON DELETE CASCADE,
	"valor" numeric(14, 2) NOT NULL
);

ALTER TABLE "repasses_locacao" ADD COLUMN IF NOT EXISTS "desconto_manutencao" numeric(14, 2) DEFAULT 0 NOT NULL;

-- conserto cobrado do inquilino entra na fatura como item próprio (não vai para o repasse: reembolsa a imobiliária)
ALTER TABLE "fatura_locacao_itens" DROP CONSTRAINT IF EXISTS "fatura_locacao_itens_tipo_check";
ALTER TABLE "fatura_locacao_itens" ADD CONSTRAINT "fatura_locacao_itens_tipo_check" CHECK ("tipo" IN ('ALUGUEL', 'CONDOMINIO', 'IPTU',
	'SEGURO_INCENDIO', 'DESCONTO', 'OUTRO', 'MULTA_RESCISORIA', 'AVISO_PREVIO', 'VISTORIA', 'CAUCAO', 'MANUTENCAO'));

ALTER TYPE "public"."tipo_alerta" ADD VALUE IF NOT EXISTS 'MANUTENCAO';
ALTER TABLE "alertas" ADD COLUMN IF NOT EXISTS "chamado_manutencao_id" uuid REFERENCES "chamados_manutencao"("id") ON DELETE CASCADE;

-- categorias do financeiro da imobiliária para o pagamento ao prestador e o reembolso do inquilino
INSERT INTO "categorias_empresa" ("nome", "tipo", "grupo") VALUES
	('Despesa paga por conta do inquilino', 'SAIDA', 'TERCEIROS'),
	('Reembolso de despesa do inquilino', 'ENTRADA', 'TERCEIROS'),
	('Manutenção de imóveis', 'SAIDA', 'DESPESA')
ON CONFLICT DO NOTHING;

-- conta do financeiro do imóvel para consertos de imóveis próprios/sócios (cobrança "Conta do imóvel")
INSERT INTO "contas_orcamentarias" ("nome", "tipo") VALUES ('Manutenção', 'DESPESA') ON CONFLICT ("nome") DO NOTHING;
