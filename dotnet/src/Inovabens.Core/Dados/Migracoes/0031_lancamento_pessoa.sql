-- Pessoa do lançamento financeiro (quem pagou ou recebeu): nome digitado livremente e, quando bate com uma pessoa
-- cadastrada, o vínculo com ela.
ALTER TABLE "lancamentos_financeiros" ADD COLUMN "pessoa_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL;
ALTER TABLE "lancamentos_financeiros" ADD COLUMN "pessoa_nome" varchar(200);
CREATE INDEX "ix_lanc_pessoa" ON "lancamentos_financeiros" ("pessoa_id") WHERE "pessoa_id" IS NOT NULL;
