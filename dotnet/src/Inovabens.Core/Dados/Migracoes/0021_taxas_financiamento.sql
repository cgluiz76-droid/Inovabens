-- Taxas médias mensais de financiamento imobiliário por banco (Banco Central, "taxas de mercado - pós-fixado em TR"),
-- usadas no comparativo do simulador do site. Fração: 0,1205 = 12,05% ao ano.
CREATE TABLE "taxas_financiamento" (
	"cnpj8" varchar(8) NOT NULL,
	"competencia" date NOT NULL,
	"instituicao" varchar(200) NOT NULL,
	"taxa_mes" numeric(10, 6) NOT NULL,
	"taxa_ano" numeric(10, 6) NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	PRIMARY KEY ("cnpj8", "competencia")
);
