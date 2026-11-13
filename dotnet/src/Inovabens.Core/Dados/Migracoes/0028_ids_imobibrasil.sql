-- Origem no painel do Imobibrasil (importação completa antes de desligar o Imobibrasil).
-- Reimportar atualiza o que já veio em vez de duplicar.

-- Código da pessoa no painel (clientes_detalhes.php "Código 2322032").
ALTER TABLE "pessoas" ADD COLUMN "imobibrasil_id" bigint;
CREATE UNIQUE INDEX "pessoas_imobibrasil_unico" ON "pessoas" ("imobibrasil_id") WHERE "imobibrasil_id" IS NOT NULL AND "excluido_em" IS NULL;

-- Lead da caixa de entrada ("lead:1154924") ou negócio do funil ("negocio:576282").
ALTER TABLE "leads" ADD COLUMN "imobibrasil_ref" varchar(30);
CREATE UNIQUE INDEX "leads_imobibrasil_unico" ON "leads" ("imobibrasil_ref") WHERE "imobibrasil_ref" IS NOT NULL AND "excluido_em" IS NULL;

-- Compromisso da agenda do painel.
ALTER TABLE "compromissos" ADD COLUMN "imobibrasil_id" bigint;
CREATE UNIQUE INDEX "compromissos_imobibrasil_unico" ON "compromissos" ("imobibrasil_id") WHERE "imobibrasil_id" IS NOT NULL AND "excluido_em" IS NULL;
