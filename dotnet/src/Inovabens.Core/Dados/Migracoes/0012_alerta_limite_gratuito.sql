-- Alerta para os administradores quando um serviço pago por uso (hoje, as fotos públicas no Cloudflare R2) chega perto
-- do limite gratuito. Um alerta aberto por usuário (conferido pelo serviço; o valor novo do enum não pode ser usado
-- num índice na mesma transação em que é criado).
ALTER TYPE "tipo_alerta" ADD VALUE IF NOT EXISTS 'LIMITE_GRATUITO';
