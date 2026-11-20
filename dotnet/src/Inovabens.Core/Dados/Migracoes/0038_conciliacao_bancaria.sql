-- Financeiro da Imobiliária, fases 2 a 4:
--   extrato_importacoes / extrato_itens  extrato OFX importado (Itaú, Sicoob, Sicredi), linha a linha;
--   regras_conciliacao                   o que o sistema aprendeu (descrição do banco → categoria);
--   fechamentos_financeiros              meses fechados: nada com data nesses meses muda até reabrir;
--   lancamentos_*.extrato_item_id        a linha do extrato que confirma o lançamento (conciliação);
--   lancamentos_financeiros.conta_bancaria_id  despesa/receita de imóvel paga pela conta da imobiliária.
CREATE TABLE "extrato_importacoes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"conta_id" uuid NOT NULL REFERENCES "contas_bancarias"("id") ON DELETE CASCADE,
	"arquivo" varchar(200) NOT NULL,
	"de" date,
	"ate" date,
	-- saldo informado pelo banco no arquivo: serve para conferir o saldo do sistema
	"saldo_banco" numeric(15,2),
	"data_saldo" date,
	"itens" integer DEFAULT 0 NOT NULL,
	"novos" integer DEFAULT 0 NOT NULL,
	"importado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"importado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE INDEX IF NOT EXISTS "ix_extrato_importacoes_conta" ON "extrato_importacoes" ("conta_id", "importado_em");

CREATE TABLE "extrato_itens" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"conta_id" uuid NOT NULL REFERENCES "contas_bancarias"("id") ON DELETE CASCADE,
	"importacao_id" uuid NOT NULL REFERENCES "extrato_importacoes"("id") ON DELETE CASCADE,
	-- identificador da transação no banco (FITID): importar o mesmo arquivo de novo não duplica
	"fit_id" varchar(120) NOT NULL,
	"data" date NOT NULL,
	"valor" numeric(15,2) NOT NULL,
	"descricao" varchar(300) NOT NULL,
	"documento" varchar(60),
	-- conciliado = confirmado por lançamento(s); ignorado = não vira lançamento (ex.: já está no saldo inicial)
	"conciliado_em" timestamp with time zone,
	"ignorado_em" timestamp with time zone,
	"conciliado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "ux_extrato_itens_fit" ON "extrato_itens" ("conta_id", "fit_id");
CREATE INDEX IF NOT EXISTS "ix_extrato_itens_pendentes" ON "extrato_itens" ("conta_id", "data") WHERE "conciliado_em" IS NULL AND "ignorado_em" IS NULL;

CREATE TABLE "regras_conciliacao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	-- descrição do banco normalizada (sem acento, número e pontuação)
	"chave" varchar(120) NOT NULL,
	"tipo" varchar(10) NOT NULL CHECK ("tipo" IN ('ENTRADA', 'SAIDA')),
	"categoria_id" uuid NOT NULL REFERENCES "categorias_empresa"("id") ON DELETE CASCADE,
	"pessoa_nome" varchar(200),
	"usos" integer DEFAULT 1 NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "ux_regras_conciliacao" ON "regras_conciliacao" ("chave", "tipo");

CREATE TABLE "fechamentos_financeiros" (
	"mes" date PRIMARY KEY NOT NULL CHECK (extract(day FROM "mes") = 1),
	"fechado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"fechado_em" timestamp with time zone DEFAULT now() NOT NULL
);

ALTER TABLE "lancamentos_empresa" ADD COLUMN IF NOT EXISTS "extrato_item_id" uuid REFERENCES "extrato_itens"("id") ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS "ix_lancamentos_empresa_extrato" ON "lancamentos_empresa" ("extrato_item_id") WHERE "extrato_item_id" IS NOT NULL;

ALTER TABLE "lancamentos_financeiros" ADD COLUMN IF NOT EXISTS "conta_bancaria_id" uuid REFERENCES "contas_bancarias"("id") ON DELETE SET NULL;
ALTER TABLE "lancamentos_financeiros" ADD COLUMN IF NOT EXISTS "extrato_item_id" uuid REFERENCES "extrato_itens"("id") ON DELETE SET NULL;
CREATE INDEX IF NOT EXISTS "ix_lancamentos_financeiros_conta_bancaria" ON "lancamentos_financeiros" ("conta_bancaria_id") WHERE "conta_bancaria_id" IS NOT NULL;
