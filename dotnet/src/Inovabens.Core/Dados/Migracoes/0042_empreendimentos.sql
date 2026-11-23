-- Empreendimento: área com uma matrícula de origem desmembrada em lotes, cada um com matrícula, preço e situação
-- próprios. O empreendimento é o próprio imóvel de origem (anúncio, fotos, centro de custo); cada lote ou área
-- remanescente é um imóvel ligado a ele. A remanescente pode ser desmembrada de novo (fase 2, 3...), então cada
-- unidade guarda a matrícula de onde saiu (origem_id) e o desmembramento que a criou.
CREATE TABLE "desmembramentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"empreendimento_id" uuid NOT NULL REFERENCES "imoveis"("id"),
	-- matrícula dividida: o empreendimento (fase 1) ou uma área remanescente (fases seguintes)
	"origem_id" uuid NOT NULL REFERENCES "imoveis"("id"),
	"fase" integer NOT NULL,
	-- PROJETO, APROVADO_PREFEITURA, PROTOCOLADO, REGISTRADO
	"situacao" varchar(30) DEFAULT 'PROJETO' NOT NULL,
	"protocolo" varchar(60),
	"data_registro" date,
	-- ruas e áreas públicas doadas ao município (não são vendidas; entram só na conferência de áreas)
	"area_doada_m2" numeric(14, 2),
	"observacao" text,
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
-- cada matrícula é desmembrada uma vez só; a fase é única no empreendimento
CREATE UNIQUE INDEX IF NOT EXISTS "desmembramentos_origem_uq" ON "desmembramentos" ("origem_id");
CREATE UNIQUE INDEX IF NOT EXISTS "desmembramentos_fase_uq" ON "desmembramentos" ("empreendimento_id", "fase");

ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "empreendimento_id" uuid REFERENCES "imoveis"("id");
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "origem_id" uuid REFERENCES "imoveis"("id");
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "desmembramento_id" uuid REFERENCES "desmembramentos"("id");
-- LOTE ou REMANESCENTE; null = imóvel comum ou o próprio empreendimento
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "unidade_tipo" varchar(20);
-- como o lote é chamado na matrícula e no anúncio: "Lote 3", "Lote 3 - Quadra A", "Área remanescente"
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "unidade_identificacao" varchar(60);
-- número do código (TE0043-07 = 7; R2 = 2), para continuar a numeração nas fases seguintes
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "unidade_numero" integer;
CREATE INDEX IF NOT EXISTS "imoveis_empreendimento_idx" ON "imoveis" ("empreendimento_id") WHERE "empreendimento_id" IS NOT NULL;
