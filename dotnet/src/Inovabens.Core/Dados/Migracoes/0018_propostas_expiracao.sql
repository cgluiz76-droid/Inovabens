-- Propostas vencidas (validade passou e ainda em análise) viram EXPIRADA pela rotina diária e avisam o responsável no sino.
ALTER TYPE "tipo_alerta" ADD VALUE IF NOT EXISTS 'PROPOSTA_EXPIRADA';

-- Busca da rotina: só as em análise com validade.
CREATE INDEX IF NOT EXISTS "ix_propostas_validade_em_analise" ON "propostas" ("validade_em") WHERE "status" = 'EM_ANALISE' AND "validade_em" IS NOT NULL;
