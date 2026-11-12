-- Alerta de lead/processo parado além do prazo da etapa (usado só a partir da 0026, em outra transação)
ALTER TYPE "public"."tipo_alerta" ADD VALUE IF NOT EXISTS 'ETAPA_PARADA';
