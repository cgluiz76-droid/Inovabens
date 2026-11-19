-- Histórico das atividades da agenda: cada conclusão, cancelamento ou mudança de data guarda a justificativa
-- (obrigatória), quem fez e a data anterior/nova. Fica separado da descrição para não se perder numa edição e para
-- o gestor ver quantas vezes uma atividade foi adiada.
CREATE TABLE "compromisso_historico" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"compromisso_id" uuid NOT NULL REFERENCES "compromissos"("id") ON DELETE CASCADE,
	"acao" varchar(20) NOT NULL,
	"justificativa" text NOT NULL,
	"inicio_anterior" timestamp with time zone,
	"inicio_novo" timestamp with time zone,
	"usuario_id" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX "ix_compromisso_historico" ON "compromisso_historico" ("compromisso_id", "criado_em");
