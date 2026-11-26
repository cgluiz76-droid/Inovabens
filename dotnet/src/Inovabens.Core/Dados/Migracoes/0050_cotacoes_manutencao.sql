-- Manutenção: arquivo da cotação (PDF/foto) de cada orçamento, guardado na subpasta "Cotações" do chamado no Drive.
-- O proprietário vê e baixa as cotações pelo link de aprovação (mínimo de 3 para ele escolher).
ALTER TABLE "orcamentos_manutencao" ADD COLUMN IF NOT EXISTS "arquivo_id" uuid REFERENCES "chamado_manutencao_arquivos"("id") ON DELETE SET NULL;
