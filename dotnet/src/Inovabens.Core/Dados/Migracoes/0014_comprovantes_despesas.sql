-- Comprovantes também na própria despesa (contrato, boleto, nota recebida antes do pagamento), além da receita e do pagamento.
ALTER TABLE "financeiro_comprovantes" ADD COLUMN "despesa_id" uuid REFERENCES "despesas"("id") ON DELETE CASCADE;
ALTER TABLE "financeiro_comprovantes" DROP CONSTRAINT "ck_comprovante_origem";
ALTER TABLE "financeiro_comprovantes"
	ADD CONSTRAINT "ck_comprovante_origem" CHECK (num_nonnulls("receita_id", "lancamento_id", "despesa_id") = 1);
CREATE INDEX "ix_comprovantes_despesa" ON "financeiro_comprovantes" ("despesa_id") WHERE "despesa_id" IS NOT NULL;
