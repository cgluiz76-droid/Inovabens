-- Contas de consumo do imóvel (Dados do Imóvel): concessionárias de água e luz e o código de cada conta
-- (código do cliente / instalação), para conferir faturas e pedir ligação, troca de titularidade ou desligamento.
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "concessionaria_agua" varchar(120);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "codigo_conta_agua" varchar(60);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "concessionaria_luz" varchar(120);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "codigo_conta_luz" varchar(60);
