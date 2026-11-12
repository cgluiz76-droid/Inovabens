-- Automações do CRM: prazo por etapa, próximo passo sugerido por etapa e metas mensais por usuário.
ALTER TABLE "funil_estagios" ADD COLUMN "prazo_dias" integer CHECK ("prazo_dias" IS NULL OR "prazo_dias" > 0);
ALTER TABLE "funil_estagios" ADD COLUMN "sugestao_tipo" "tipo_compromisso";
ALTER TABLE "funil_estagios" ADD COLUMN "sugestao_titulo" varchar(200);
ALTER TABLE "funil_estagios" ADD COLUMN "sugestao_dias" integer CHECK ("sugestao_dias" IS NULL OR "sugestao_dias" BETWEEN 0 AND 365);

ALTER TABLE "alertas" ADD COLUMN "processo_id" uuid REFERENCES "processos"("id") ON DELETE CASCADE;
CREATE INDEX "ix_alertas_etapa_parada" ON "alertas" ("tipo", "lead_id", "processo_id") WHERE "resolvido_em" IS NULL;

CREATE TABLE "metas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"usuario_id" uuid NOT NULL REFERENCES "usuarios"("id") ON DELETE CASCADE,
	"mes" date NOT NULL CHECK (extract(day FROM "mes") = 1),
	"atendimentos" integer DEFAULT 0 NOT NULL CHECK ("atendimentos" >= 0),
	"propostas" integer DEFAULT 0 NOT NULL CHECK ("propostas" >= 0),
	"fechamentos" integer DEFAULT 0 NOT NULL CHECK ("fechamentos" >= 0),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE UNIQUE INDEX "ux_meta_usuario_mes" ON "metas" ("usuario_id", "mes");

-- Próximo passo sugerido nas etapas padrão (as mesmas de PadroesIniciais); só onde ainda não há sugestão
UPDATE "funil_estagios" e SET "sugestao_tipo" = s.tipo::"tipo_compromisso", "sugestao_titulo" = s.titulo, "sugestao_dias" = s.dias
FROM (VALUES
	('VENDA', 'Novo Lead', 'LIGACAO', 'Primeiro contato com o cliente', 0),
	('VENDA', 'Contato Realizado', 'VISITA', 'Agendar visita ao imóvel', 2),
	('VENDA', 'Visita Agendada', 'VISITA', 'Visita ao imóvel', 2),
	('VENDA', 'Proposta', 'LIGACAO', 'Retorno sobre a proposta', 2),
	('VENDA', 'Negociação', 'LIGACAO', 'Acompanhar a negociação', 2),
	('VENDA', 'Documentação/Fechamento', 'TAREFA', 'Conferir a documentação', 3),
	('VENDA', 'Concluído', 'TAREFA', 'Pós-venda: contato com o cliente', 7),
	('LOCACAO', 'Novo Lead', 'LIGACAO', 'Primeiro contato com o cliente', 0),
	('LOCACAO', 'Contato Realizado', 'VISITA', 'Agendar visita ao imóvel', 2),
	('LOCACAO', 'Visita Agendada', 'VISITA', 'Visita ao imóvel', 2),
	('LOCACAO', 'Proposta', 'LIGACAO', 'Retorno sobre a proposta', 2),
	('LOCACAO', 'Negociação', 'LIGACAO', 'Acompanhar a negociação', 2),
	('LOCACAO', 'Documentação/Fechamento', 'TAREFA', 'Conferir a documentação e a garantia', 3),
	('LOCACAO', 'Concluído', 'TAREFA', 'Pós-locação: contato com o inquilino', 7),
	('CAPTACAO', 'Contato', 'LIGACAO', 'Conversar com o proprietário', 0),
	('CAPTACAO', 'Avaliação', 'VISITA', 'Avaliar o imóvel', 2),
	('CAPTACAO', 'Documentos', 'TAREFA', 'Receber os documentos do imóvel', 3),
	('CAPTACAO', 'Captado', 'TAREFA', 'Cadastrar o imóvel e as fotos', 1)
) AS s(funil, nome, tipo, titulo, dias)
WHERE e."funil" = s.funil::"funil" AND e."nome" = s.nome AND e."sugestao_titulo" IS NULL;
