-- Locação, fase 7: boleto com Pix pelo Banco Cora (baixa automática pelo webhook) e portal do inquilino e do
-- proprietário por link seguro, sem senha.

-- boleto emitido no Cora para uma fatura (um ativo por vez; reemitir cancela o anterior)
CREATE TABLE "fatura_boletos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"fatura_id" uuid NOT NULL REFERENCES "faturas_locacao"("id") ON DELETE CASCADE,
	"provedor" varchar(20) NOT NULL DEFAULT 'CORA',
	-- id do boleto no banco (inv_...) e a chave de idempotência usada na emissão
	"externo_id" varchar(100) NOT NULL UNIQUE,
	"idempotencia" uuid NOT NULL,
	-- situação no banco: OPEN, LATE, PAID, CANCELLED, IN_PAYMENT...
	"status" varchar(30) NOT NULL,
	"valor" numeric(14, 2) NOT NULL CHECK ("valor" > 0),
	"vencimento" date NOT NULL,
	"linha_digitavel" varchar(80),
	"codigo_barras" varchar(80),
	"pix_copia_cola" varchar(1000),
	"url_pdf" varchar(1000),
	-- pagamento confirmado: valores do banco e o recebimento lançado na fatura
	"pago_em" date,
	"valor_pago" numeric(14, 2),
	"forma_pagamento" varchar(20),
	"pagamento_id" uuid REFERENCES "fatura_locacao_pagamentos"("id") ON DELETE SET NULL,
	"erro" varchar(500),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"cancelado_em" timestamp with time zone
);

CREATE INDEX IF NOT EXISTS "ix_fatura_boletos_fatura" ON "fatura_boletos" ("fatura_id");

-- links do portal: inquilino (por contrato) ou proprietário (por pessoa)
CREATE TABLE "portal_acessos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"token" varchar(64) NOT NULL UNIQUE,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('INQUILINO', 'PROPRIETARIO')),
	"contrato_id" uuid REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	"pessoa_id" uuid REFERENCES "pessoas"("id") ON DELETE CASCADE,
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"revogado_em" timestamp with time zone,
	"ultimo_acesso" timestamp with time zone,
	"acessos" integer DEFAULT 0 NOT NULL,
	CHECK (("tipo" = 'INQUILINO' AND "contrato_id" IS NOT NULL) OR ("tipo" = 'PROPRIETARIO' AND "pessoa_id" IS NOT NULL))
);

CREATE INDEX IF NOT EXISTS "ix_portal_acessos_contrato" ON "portal_acessos" ("contrato_id");
CREATE INDEX IF NOT EXISTS "ix_portal_acessos_pessoa" ON "portal_acessos" ("pessoa_id");
