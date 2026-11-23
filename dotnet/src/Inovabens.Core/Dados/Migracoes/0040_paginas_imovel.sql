-- Página exclusiva do imóvel (landing page): um endereço amigável (/imovel/<slug>) para mandar por WhatsApp, e-mail ou
-- QR code a clientes e investidores, aberto sem login quando publicada. Uma por imóvel. O conteúdo (números de
-- destaque, seções, diferenciais, perguntas, condições) fica em jsonb: são blocos que a tela edita juntos.
-- Só entra no Google com o domínio próprio ligado (SITE_PUBLICO_URL); antes disso as páginas levam noindex.
CREATE TABLE "paginas_imovel" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"slug" varchar(100) NOT NULL,
	"publicada" boolean DEFAULT false NOT NULL,
	"titulo" varchar(120) NOT NULL,
	"chamada" varchar(300),
	"titulo_seo" varchar(70),
	"descricao_seo" varchar(170),
	-- foto de abertura e da prévia no WhatsApp (uma das fotos públicas do imóvel); null = a capa do imóvel
	"foto_capa_id" uuid REFERENCES "imovel_midias"("id") ON DELETE SET NULL,
	"conteudo" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"publicada_em" timestamp with time zone,
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "paginas_imovel_imovel_uq" ON "paginas_imovel" ("imovel_id");
-- o endereço é único e não muda de dono: link já enviado nunca abre outro imóvel
CREATE UNIQUE INDEX IF NOT EXISTS "paginas_imovel_slug_uq" ON "paginas_imovel" ("slug");
