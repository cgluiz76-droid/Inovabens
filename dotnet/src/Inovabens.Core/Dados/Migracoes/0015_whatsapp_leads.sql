-- WhatsApp (Cloud API da Meta ou parceiro): cada mensagem recebida cria ou alimenta um lead no CRM.
-- A mensagem fica guardada uma única vez (id da Meta), o que torna o webhook idempotente quando a Meta reenvia.
CREATE TABLE "whatsapp_mensagens" (
	"id" varchar(160) PRIMARY KEY NOT NULL,
	"telefone" varchar(20) NOT NULL,
	"nome_contato" varchar(200),
	"direcao" varchar(10) NOT NULL,
	"tipo" varchar(30) NOT NULL,
	"texto" text,
	"pessoa_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"lead_id" uuid REFERENCES "leads"("id") ON DELETE SET NULL,
	"enviada_em" timestamp with time zone NOT NULL,
	"recebida_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "ck_whatsapp_direcao" CHECK ("direcao" IN ('ENTRADA', 'SAIDA'))
);
CREATE INDEX "ix_whatsapp_mensagens_lead" ON "whatsapp_mensagens" ("lead_id") WHERE "lead_id" IS NOT NULL;
CREATE INDEX "ix_whatsapp_mensagens_telefone" ON "whatsapp_mensagens" ("telefone");

-- imóvel citado na mensagem (ex.: enviada pelo botão do site): o usuário confirma o vínculo no CRM
ALTER TABLE "leads" ADD COLUMN "imovel_sugerido_id" uuid REFERENCES "imoveis"("id") ON DELETE SET NULL;

-- alerta "novo lead" no sino, resolvido quando o lead ganha um imóvel de interesse
ALTER TABLE "alertas" ADD COLUMN "lead_id" uuid REFERENCES "leads"("id") ON DELETE CASCADE;
ALTER TYPE "tipo_alerta" ADD VALUE IF NOT EXISTS 'NOVO_LEAD';
