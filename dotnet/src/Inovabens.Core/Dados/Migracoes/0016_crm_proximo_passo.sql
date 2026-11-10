-- CRM: toda ação registrada no lead pede o próximo passo (um compromisso na agenda) ou o motivo de não agendar.

-- lead sem imóvel de interesse também agenda atividades (ligação, mensagem, reunião…)
ALTER TABLE "compromissos" ALTER COLUMN "imovel_id" DROP NOT NULL;
ALTER TABLE "compromissos" ADD CONSTRAINT "ck_compromissos_imovel_ou_lead" CHECK ("imovel_id" IS NOT NULL OR "lead_id" IS NOT NULL);
CREATE INDEX IF NOT EXISTS "ix_compromissos_lead_pendente" ON "compromissos" ("lead_id", "inicio_em") WHERE "lead_id" IS NOT NULL AND "status" = 'PENDENTE' AND "excluido_em" IS NULL;

-- novos tipos de atividade
ALTER TYPE "tipo_compromisso" ADD VALUE IF NOT EXISTS 'LIGACAO';
ALTER TYPE "tipo_compromisso" ADD VALUE IF NOT EXISTS 'MENSAGEM';

-- a interação guarda o próximo passo criado ou o motivo de ter pulado
ALTER TABLE "lead_interacoes" ADD COLUMN "proximo_compromisso_id" uuid REFERENCES "compromissos"("id") ON DELETE SET NULL;
ALTER TABLE "lead_interacoes" ADD COLUMN "motivo_sem_proximo" varchar(500);
ALTER TABLE "lead_interacoes" ADD COLUMN "compromisso_concluido_id" uuid REFERENCES "compromissos"("id") ON DELETE SET NULL;

-- ações que não são contato com o cliente (mudança de estágio, conclusão de atividade sem canal)
ALTER TYPE "canal_interacao" ADD VALUE IF NOT EXISTS 'OUTRO';
