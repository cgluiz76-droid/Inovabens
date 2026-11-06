-- Comprovantes (notas, recibos, boletos pagos) anexados às receitas e aos pagamentos de despesas.
-- O arquivo fica no Google Drive, em "<pasta do imóvel>/07 - Financeiro/<ano>"; aqui só o vínculo.
CREATE TABLE "financeiro_comprovantes" (
	"id" uuid PRIMARY KEY NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"receita_id" uuid REFERENCES "receitas"("id") ON DELETE CASCADE,
	"lancamento_id" uuid REFERENCES "despesa_lancamentos"("id") ON DELETE CASCADE,
	"arquivo_chave" text NOT NULL,
	"nome_original" varchar(255) NOT NULL,
	"tipo_conteudo" varchar(120) NOT NULL,
	"tamanho" bigint NOT NULL,
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ck_comprovante_origem" CHECK (num_nonnulls("receita_id", "lancamento_id") = 1)
);
CREATE INDEX "ix_comprovantes_imovel" ON "financeiro_comprovantes" ("imovel_id");
CREATE INDEX "ix_comprovantes_receita" ON "financeiro_comprovantes" ("receita_id") WHERE "receita_id" IS NOT NULL;
CREATE INDEX "ix_comprovantes_lancamento" ON "financeiro_comprovantes" ("lancamento_id") WHERE "lancamento_id" IS NOT NULL;
