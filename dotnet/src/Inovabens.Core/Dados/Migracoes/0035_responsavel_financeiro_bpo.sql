-- Financeiro/BPO: responsável financeiro do imóvel (quem recebe os lançamentos pelo WhatsApp) e o registro de
-- "enviado ao BPO" em cada lançamento (quando e por quem). O envio hoje é manual (cartão copiado/PDF); quando a
-- API da Meta estiver configurada, o envio automático preenche os mesmos campos.
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "bpo_nome" varchar(200);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "bpo_whatsapp" varchar(30);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "bpo_email" varchar(200);
ALTER TABLE "lancamentos_financeiros" ADD COLUMN IF NOT EXISTS "bpo_enviado_em" timestamptz;
ALTER TABLE "lancamentos_financeiros" ADD COLUMN IF NOT EXISTS "bpo_enviado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL;
