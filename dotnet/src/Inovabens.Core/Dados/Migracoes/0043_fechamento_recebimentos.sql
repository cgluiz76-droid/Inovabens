-- Formas de recebimento do fechamento (venda): dinheiro, parcelado, imóvel em permuta ou outro bem. A soma dos valores
-- é o valor da venda; fechamento sem linhas = tudo em dinheiro (os já existentes). Na permuta, "valor" é a parte da
-- venda paga pelo imóvel recebido e "valor_bem" o valor combinado dele: a diferença (torna paga) vai para o custo do
-- imóvel recebido, que vira um imóvel novo com aquisição própria (imovel_recebido_id).
CREATE TABLE "fechamento_recebimentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "fechamentos_investimento"("imovel_id") ON DELETE CASCADE,
	"ordem" integer DEFAULT 0 NOT NULL,
	-- DINHEIRO, PARCELADO, PERMUTA_IMOVEL, OUTRO_BEM
	"tipo" varchar(20) NOT NULL,
	"valor" numeric(15, 2) NOT NULL,
	"valor_bem" numeric(15, 2),
	"descricao" varchar(200),
	"parcelas" integer,
	"primeiro_vencimento" date,
	"imovel_recebido_id" uuid REFERENCES "imoveis"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ck_fech_receb_valores" CHECK ("valor" > 0 AND ("valor_bem" IS NULL OR "valor_bem" > 0) AND ("parcelas" IS NULL OR "parcelas" > 0))
);
CREATE INDEX IF NOT EXISTS "fechamento_recebimentos_imovel_idx" ON "fechamento_recebimentos" ("imovel_id");
