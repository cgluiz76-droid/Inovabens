-- Manutenção: com menos de 3 cotações, o pedido ao proprietário segue com uma justificativa (mostrada a ele no link).
ALTER TABLE "chamados_manutencao" ADD COLUMN IF NOT EXISTS "justificativa_cotacoes" varchar(1000);
