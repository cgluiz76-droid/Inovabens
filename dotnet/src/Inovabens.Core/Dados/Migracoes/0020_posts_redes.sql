-- Posts dos imóveis para redes sociais: textos por rede (gerados pelo Claude), peças geradas (imagens, clipes do
-- Magnific, música e vídeo montado no R2) e publicações feitas pela Graph API da Meta.
CREATE TABLE "posts_redes" (
	"id" uuid PRIMARY KEY NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"finalidade" "finalidade_imovel" NOT NULL,
	"textos" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"expandir_com_ia" boolean DEFAULT false NOT NULL,
	"com_video" boolean DEFAULT false NOT NULL,
	"criado_por" uuid REFERENCES "usuarios"("id"),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE INDEX "posts_redes_imovel_idx" ON "posts_redes" ("imovel_id", "criado_em" DESC);

CREATE TABLE "posts_redes_midias" (
	"id" uuid PRIMARY KEY NOT NULL,
	"post_id" uuid NOT NULL REFERENCES "posts_redes"("id") ON DELETE CASCADE,
	"tipo" varchar(10) NOT NULL CHECK ("tipo" IN ('FEED', 'STORY', 'CLIPE', 'MUSICA', 'VIDEO')),
	"status" varchar(12) DEFAULT 'PENDENTE' NOT NULL CHECK ("status" IN ('PENDENTE', 'PROCESSANDO', 'CONCLUIDA', 'FALHA')),
	"etapa" varchar(10) CHECK ("etapa" IN ('EXPANDIR', 'ANIMAR', 'MUSICA')),
	"tarefa" varchar(80),
	"midia_origem_id" uuid REFERENCES "imovel_midias"("id") ON DELETE SET NULL,
	"ordem" integer DEFAULT 0 NOT NULL,
	"capa" boolean DEFAULT false NOT NULL,
	"chave" varchar(300),
	"content_type" varchar(60),
	"erro" text,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE INDEX "posts_redes_midias_post_idx" ON "posts_redes_midias" ("post_id", "tipo", "ordem");
-- a rotina procura o que falta processar
CREATE INDEX "posts_redes_midias_pendentes_idx" ON "posts_redes_midias" ("status") WHERE "status" IN ('PENDENTE', 'PROCESSANDO');

CREATE TABLE "posts_redes_publicacoes" (
	"id" uuid PRIMARY KEY NOT NULL,
	"post_id" uuid NOT NULL REFERENCES "posts_redes"("id") ON DELETE CASCADE,
	"rede" varchar(20) NOT NULL CHECK ("rede" IN ('INSTAGRAM_FEED', 'INSTAGRAM_STORY', 'INSTAGRAM_REELS', 'FACEBOOK_FEED', 'FACEBOOK_VIDEO')),
	"status" varchar(12) DEFAULT 'AGUARDANDO' NOT NULL CHECK ("status" IN ('AGUARDANDO', 'PUBLICADA', 'FALHA')),
	"container" varchar(80),
	"id_externo" varchar(80),
	"link" varchar(500),
	"erro" text,
	"publicado_por" uuid REFERENCES "usuarios"("id"),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"publicado_em" timestamp with time zone
);
CREATE INDEX "posts_redes_publicacoes_post_idx" ON "posts_redes_publicacoes" ("post_id");
