-- Importação do Notion (workspace "Inova Bens"): perfil Equipe, dados do projeto no imóvel e o registro do que já
-- foi trazido (para reimportar sem duplicar e sem sobrescrever o que foi editado no sistema).

INSERT INTO perfis_pessoa (codigo, nome, ativo, ordem)
VALUES ('EQUIPE', 'Equipe', true, (SELECT coalesce(max(ordem), 0) + 1 FROM perfis_pessoa))
ON CONFLICT (codigo) DO NOTHING;

-- Acompanhamento do projeto (engenharia), como no Notion: status, etapa da estratégia e previsão de entrega
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "projeto_status" varchar(30);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "projeto_etapa" varchar(40);
ALTER TABLE "imoveis" ADD COLUMN IF NOT EXISTS "projeto_previsao_entrega" date;

-- Um registro por item do Notion já importado (página, linha de tabela, arquivo) e o que ele virou no sistema.
-- tipo: PESSOA, IMOVEL, MEMORANDO, COMPROMISSO, ARQUIVO
CREATE TABLE "notion_importacoes" (
	"notion_id" varchar(80) NOT NULL,
	"tipo" varchar(20) NOT NULL,
	"entidade_id" uuid NOT NULL,
	"editado_notion_em" timestamp with time zone,
	"importado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "notion_importacoes_pk" PRIMARY KEY ("notion_id", "tipo")
);
CREATE INDEX "ix_notion_importacoes_entidade" ON "notion_importacoes" ("entidade_id");
