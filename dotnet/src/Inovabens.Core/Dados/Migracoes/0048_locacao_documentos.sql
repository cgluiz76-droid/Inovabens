-- Locação: pasta "08 - Locação" no Drive do imóvel, subpasta por contrato e modelos Word de contrato e de acordo.

-- modelos de locação; "padrao" = gerado pelo sistema (recriado se apagado; o usuário pode enviar outros e desativar este)
ALTER TYPE "tipo_modelo_documento" ADD VALUE IF NOT EXISTS 'CONTRATO_LOCACAO';
ALTER TYPE "tipo_modelo_documento" ADD VALUE IF NOT EXISTS 'ACORDO_LOCACAO';
ALTER TABLE "modelos_documento" ADD COLUMN IF NOT EXISTS "padrao" boolean NOT NULL DEFAULT false;

-- "08 - Locação" criada (imóvel para alugar) e subpasta do contrato (renovações ficam na mesma)
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "drive_pasta_locacao_id" varchar(200);
ALTER TABLE "contratos_locacao" ADD COLUMN IF NOT EXISTS "drive_pasta_id" varchar(200);

-- acordo de atraso e documentos gerados entram no histórico do contrato
ALTER TABLE "contrato_locacao_historico" DROP CONSTRAINT IF EXISTS "contrato_locacao_historico_tipo_check";
ALTER TABLE "contrato_locacao_historico" ADD CONSTRAINT "contrato_locacao_historico_tipo_check" CHECK ("tipo" IN ('REAJUSTE', 'ADITIVO',
	'RENOVACAO', 'RESCISAO', 'PREFERENCIA', 'ACORDO', 'DOCUMENTO'));

-- dados do acordo (parcelas, desconto) para gerar o documento de novo a qualquer momento
ALTER TABLE "contrato_locacao_historico" ADD COLUMN IF NOT EXISTS "dados" jsonb;
