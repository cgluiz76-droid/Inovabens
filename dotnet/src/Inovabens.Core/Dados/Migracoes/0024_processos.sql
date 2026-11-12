-- Processos do imóvel: cada imóvel tem no máximo um cartão aberto em cada funil de processo
-- (ADMINISTRATIVO, PROJETOS), com etapa, responsável e prazo, e o histórico das mudanças de etapa.
CREATE TABLE "processos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"funil" "funil" NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id"),
	"estagio_id" uuid NOT NULL REFERENCES "funil_estagios"("id"),
	"responsavel_id" uuid REFERENCES "usuarios"("id"),
	"observacoes" text,
	"prazo" date,
	"ordem" integer DEFAULT 0 NOT NULL,
	"criado_por" uuid REFERENCES "usuarios"("id"),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone
);
CREATE UNIQUE INDEX "ux_processo_imovel_funil" ON "processos" ("imovel_id", "funil") WHERE "excluido_em" IS NULL;
CREATE INDEX "ix_processos_estagio" ON "processos" ("estagio_id");

CREATE TABLE "processo_historico" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"processo_id" uuid NOT NULL REFERENCES "processos"("id"),
	"estagio_anterior_id" uuid REFERENCES "funil_estagios"("id"),
	"estagio_novo_id" uuid NOT NULL REFERENCES "funil_estagios"("id"),
	"usuario_id" uuid REFERENCES "usuarios"("id"),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE INDEX "ix_processo_historico_processo" ON "processo_historico" ("processo_id");

-- Etapas padrão dos funis novos (os mesmos de PadroesIniciais; o administrador pode mudar depois)
INSERT INTO "funil_estagios" ("funil", "nome", "tipo", "cor", "ordem") VALUES
	('CAPTACAO', 'Contato', 'ABERTO', '#64748b', 1),
	('CAPTACAO', 'Avaliação', 'ABERTO', '#0ea5e9', 2),
	('CAPTACAO', 'Documentos', 'ABERTO', '#f59e0b', 3),
	('CAPTACAO', 'Captado', 'GANHO', '#16a34a', 4),
	('CAPTACAO', 'Descartado', 'PERDIDO', '#dc2626', 5),
	('ADMINISTRATIVO', 'Matrícula e certidões', 'ABERTO', '#64748b', 1),
	('ADMINISTRATIVO', 'Regularização', 'ABERTO', '#0ea5e9', 2),
	('ADMINISTRATIVO', 'Contrato', 'ABERTO', '#8b5cf6', 3),
	('ADMINISTRATIVO', 'Cartório/Registro', 'ABERTO', '#f59e0b', 4),
	('ADMINISTRATIVO', 'Concluído', 'GANHO', '#16a34a', 5),
	('ADMINISTRATIVO', 'Cancelado', 'PERDIDO', '#dc2626', 6),
	('PROJETOS', 'Aguardando início', 'ABERTO', '#64748b', 1),
	('PROJETOS', 'Definir estratégia', 'ABERTO', '#0ea5e9', 2),
	('PROJETOS', 'Desenvolvimento técnico', 'ABERTO', '#8b5cf6', 3),
	('PROJETOS', 'Análise externa', 'ABERTO', '#f59e0b', 4),
	('PROJETOS', 'Aprovação/registro', 'ABERTO', '#f97316', 5),
	('PROJETOS', 'Pendente cliente', 'ABERTO', '#a16207', 6),
	('PROJETOS', 'Vitrine', 'ABERTO', '#0d9488', 7),
	('PROJETOS', 'Concluído', 'GANHO', '#16a34a', 8),
	('PROJETOS', 'Cancelado', 'PERDIDO', '#dc2626', 9)
ON CONFLICT ("funil", "nome") DO NOTHING;

-- Projetos que vieram do Notion: a "etapa da estratégia" do imóvel vira o cartão do funil Projetos
-- (sem etapa: "Concluído" se o status for Concluído, senão "Aguardando início"). Os campos do imóvel continuam.
WITH origem AS (
	SELECT i."id" AS imovel_id, i."corretor_responsavel_id" AS responsavel_id,
	       coalesce(e."id", CASE WHEN i."projeto_status" = 'Concluído' THEN fim."id" ELSE ini."id" END) AS estagio_id
	FROM "imoveis" i
	LEFT JOIN "funil_estagios" e ON e."funil" = 'PROJETOS' AND e."nome" = i."projeto_etapa"
	JOIN "funil_estagios" ini ON ini."funil" = 'PROJETOS' AND ini."nome" = 'Aguardando início'
	JOIN "funil_estagios" fim ON fim."funil" = 'PROJETOS' AND fim."nome" = 'Concluído'
	WHERE i."excluido_em" IS NULL AND (i."projeto_etapa" IS NOT NULL OR i."projeto_status" IS NOT NULL)
), novos AS (
	INSERT INTO "processos" ("funil", "imovel_id", "estagio_id", "responsavel_id")
	SELECT 'PROJETOS', o.imovel_id, o.estagio_id, o.responsavel_id FROM origem o
	WHERE NOT EXISTS (SELECT 1 FROM "processos" p WHERE p."imovel_id" = o.imovel_id AND p."funil" = 'PROJETOS' AND p."excluido_em" IS NULL)
	RETURNING "id", "estagio_id"
)
INSERT INTO "processo_historico" ("processo_id", "estagio_novo_id")
SELECT "id", "estagio_id" FROM novos;
