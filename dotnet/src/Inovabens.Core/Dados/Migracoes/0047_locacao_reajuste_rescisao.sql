-- Locação, fase 3 (docs/plano-locacao.md): reajuste, renovação/aditivo, vencimentos, rescisão e direito de preferência.

-- datas que geram aviso e dados da rescisão e da notificação de preferência (art. 27)
-- aniversário do último reajuste aplicado (o próximo é 12 meses depois; sem reajuste, conta do início)
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "ultimo_reajuste" date;
-- seguro-fiança, título de capitalização ou carta-fiança vencem e precisam ser renovados
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "garantia_vence" date;
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "seguro_incendio_vence" date;
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "iniciativa_rescisao" varchar(20) CHECK ("iniciativa_rescisao" IS NULL OR "iniciativa_rescisao" IN ('LOCATARIO', 'LOCADOR', 'ACORDO', 'FIM_PRAZO'));
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "aviso_desocupacao_em" date;
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "preferencia_notificada_em" date;
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "preferencia_valor" numeric(14, 2);
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "preferencia_condicoes" varchar(1000);
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "preferencia_resposta" varchar(20) CHECK ("preferencia_resposta" IS NULL OR "preferencia_resposta" IN ('ACEITA', 'RECUSADA', 'SEM_RESPOSTA'));

-- histórico do contrato: cada mudança de valor ou prazo e os atos formais (rescisão, preferência)
CREATE TABLE "contrato_locacao_historico" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"contrato_id" uuid NOT NULL REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('REAJUSTE', 'ADITIVO', 'RENOVACAO', 'RESCISAO', 'PREFERENCIA')),
	-- data em que a mudança vale
	"data" date NOT NULL,
	"valor_anterior" numeric(14, 2),
	"valor_novo" numeric(14, 2),
	"prazo_anterior" integer,
	"prazo_novo" integer,
	"indice" "codigo_indice",
	"percentual" numeric(9, 4),
	"descricao" varchar(2000) NOT NULL,
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS "ix_contrato_locacao_historico" ON "contrato_locacao_historico" ("contrato_id", "data");

-- avisos de vencimento já dados (um por evento e antecedência), com a tarefa criada na agenda
CREATE TABLE "locacao_avisos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"contrato_id" uuid NOT NULL REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('REAJUSTE', 'FIM', 'GARANTIA', 'SEGURO_INCENDIO', 'PREFERENCIA')),
	"data_evento" date NOT NULL,
	-- dias de antecedência deste aviso (60, 30, 7)
	"antecedencia" smallint NOT NULL,
	"compromisso_id" uuid REFERENCES "compromissos"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ux_locacao_aviso" UNIQUE ("contrato_id", "tipo", "data_evento", "antecedencia")
);

ALTER TYPE "public"."tipo_alerta" ADD VALUE IF NOT EXISTS 'LOCACAO_VENCIMENTO';
ALTER TABLE "alertas" ADD COLUMN IF NOT EXISTS "locacao_aviso_id" uuid REFERENCES "locacao_avisos"("id") ON DELETE CASCADE;

-- itens do acerto da rescisão
ALTER TABLE "fatura_locacao_itens" DROP CONSTRAINT IF EXISTS "fatura_locacao_itens_tipo_check";
ALTER TABLE "fatura_locacao_itens" ADD CONSTRAINT "fatura_locacao_itens_tipo_check" CHECK ("tipo" IN ('ALUGUEL', 'CONDOMINIO', 'IPTU',
	'SEGURO_INCENDIO', 'DESCONTO', 'OUTRO', 'MULTA_RESCISORIA', 'AVISO_PREVIO', 'VISTORIA', 'CAUCAO'));

-- a fatura do acerto da rescisão fica no mês da entrega das chaves, ao lado da fatura do aluguel daquele mês
ALTER TABLE "faturas_locacao" ADD COLUMN IF NOT EXISTS "rescisao" boolean NOT NULL DEFAULT false;
DROP INDEX IF EXISTS "ux_faturas_locacao_competencia";
CREATE UNIQUE INDEX IF NOT EXISTS "ux_faturas_locacao_competencia" ON "faturas_locacao" ("contrato_id", "competencia", "rescisao") WHERE "status" <> 'CANCELADA';
