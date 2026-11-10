-- CRM: propostas e contrapropostas de compra/locação de um imóvel, com a composição do pagamento.

CREATE TYPE "status_proposta" AS ENUM ('EM_ANALISE', 'ACEITA', 'RECUSADA', 'SUBSTITUIDA', 'CANCELADA', 'EXPIRADA');
CREATE TYPE "autor_proposta" AS ENUM ('CLIENTE', 'PROPRIETARIO');
CREATE TYPE "tipo_item_proposta" AS ENUM ('ENTRADA', 'PARCELAS_PROPRIETARIO', 'FINANCIAMENTO', 'FGTS', 'CONSORCIO', 'PERMUTA_IMOVEL', 'PERMUTA_VEICULO', 'OUTRO');
CREATE TYPE "garantia_locacao" AS ENUM ('FIADOR', 'CAUCAO', 'SEGURO_FIANCA', 'TITULO_CAPITALIZACAO', 'SEM_GARANTIA');

CREATE TABLE "propostas" (
	"id" uuid PRIMARY KEY NOT NULL,
	"lead_id" uuid NOT NULL REFERENCES "leads"("id") ON DELETE CASCADE,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id"),
	"numero" integer NOT NULL,
	"anterior_id" uuid REFERENCES "propostas"("id") ON DELETE SET NULL,
	"autor" "autor_proposta" NOT NULL,
	"finalidade" "finalidade_lead" NOT NULL,
	"status" "status_proposta" DEFAULT 'EM_ANALISE' NOT NULL,
	"valor_anunciado" numeric(15, 2),
	"valor_total" numeric(15, 2) NOT NULL,
	"validade_em" date,
	"condicoes" text,
	"aluguel" numeric(15, 2),
	"garantia" "garantia_locacao",
	"prazo_meses" integer,
	"indice_reajuste" varchar(30),
	"carencia_meses" integer,
	"motivo_decisao" varchar(500),
	"decidido_por" uuid REFERENCES "usuarios"("id"),
	"decidido_em" timestamp with time zone,
	"criado_por" uuid REFERENCES "usuarios"("id"),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ck_propostas_valores" CHECK ("valor_total" >= 0 AND ("aluguel" IS NULL OR "aluguel" >= 0)),
	CONSTRAINT "ck_propostas_prazos" CHECK (("prazo_meses" IS NULL OR "prazo_meses" > 0) AND ("carencia_meses" IS NULL OR "carencia_meses" >= 0))
);
CREATE INDEX "ix_propostas_lead" ON "propostas" ("lead_id", "criado_em");
CREATE INDEX "ix_propostas_imovel" ON "propostas" ("imovel_id");
-- REGRA: uma proposta em análise por lead e imóvel (a nova é contraproposta e substitui a anterior)
CREATE UNIQUE INDEX "ux_propostas_em_analise" ON "propostas" ("lead_id", "imovel_id") WHERE "status" = 'EM_ANALISE';

CREATE TABLE "proposta_itens" (
	"id" uuid PRIMARY KEY NOT NULL,
	"proposta_id" uuid NOT NULL REFERENCES "propostas"("id") ON DELETE CASCADE,
	"ordem" integer NOT NULL,
	"tipo" "tipo_item_proposta" NOT NULL,
	"valor" numeric(15, 2) NOT NULL,
	"descricao" varchar(300),
	"instituicao" varchar(120),
	"parcelas" integer,
	"data_prevista" date,
	"credito_aprovado" boolean,
	"imovel_permuta_id" uuid REFERENCES "imoveis"("id") ON DELETE SET NULL,
	CONSTRAINT "ck_proposta_itens_valor" CHECK ("valor" > 0),
	CONSTRAINT "ck_proposta_itens_parcelas" CHECK ("parcelas" IS NULL OR "parcelas" > 0)
);
CREATE INDEX "ix_proposta_itens_proposta" ON "proposta_itens" ("proposta_id", "ordem");

-- a ação registrada no lead aponta para a proposta (linha do tempo)
ALTER TABLE "lead_interacoes" ADD COLUMN "proposta_id" uuid REFERENCES "propostas"("id") ON DELETE SET NULL;

-- estágio "Documentação/Fechamento" antes do ganho, nos bancos que já têm os estágios (o banco novo recebe pelos dados iniciais)
UPDATE "funil_estagios" e SET "ordem" = e."ordem" + 1
WHERE NOT EXISTS (SELECT 1 FROM "funil_estagios" x WHERE x."funil" = e."funil" AND x."nome" = 'Documentação/Fechamento')
  AND e."ordem" >= (SELECT min(g."ordem") FROM "funil_estagios" g WHERE g."funil" = e."funil" AND g."tipo" = 'GANHO');
INSERT INTO "funil_estagios" ("funil", "nome", "tipo", "cor", "ordem")
SELECT g."funil", 'Documentação/Fechamento', 'ABERTO', '#0d9488', min(g."ordem") - 1
FROM "funil_estagios" g
WHERE g."tipo" = 'GANHO'
  AND NOT EXISTS (SELECT 1 FROM "funil_estagios" x WHERE x."funil" = g."funil" AND x."nome" = 'Documentação/Fechamento')
GROUP BY g."funil";
