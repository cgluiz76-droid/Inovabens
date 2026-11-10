-- Modelos Word (.docx) de proposta e de contrato enviados pelo administrador, com marcadores {{campo}} preenchidos pelo sistema.
CREATE TYPE "tipo_modelo_documento" AS ENUM ('PROPOSTA', 'CONTRATO');

CREATE TABLE "modelos_documento" (
	"id" uuid PRIMARY KEY NOT NULL,
	"nome" varchar(120) NOT NULL,
	"tipo" "tipo_modelo_documento" NOT NULL,
	-- vazio = serve para venda e locação
	"finalidade" "finalidade_lead",
	"nome_arquivo" varchar(200) NOT NULL,
	"conteudo" bytea NOT NULL,
	"tamanho" integer NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL,
	"criado_por" uuid REFERENCES "usuarios"("id"),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
