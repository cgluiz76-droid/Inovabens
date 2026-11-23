-- Medição da página exclusiva do imóvel: cada visita e cada clique no WhatsApp, com a origem (utm_source dos links de
-- compartilhar: link, whatsapp, email, qrcode...; "google" ou "direto" sem utm). Sem IP, cookie ou qualquer dado do
-- visitante (LGPD): só contagem. Robôs (inclusive o leitor de prévia do WhatsApp) e usuários do sistema não contam.
CREATE TABLE "paginas_imovel_eventos" (
	"id" bigserial PRIMARY KEY,
	"pagina_id" uuid NOT NULL REFERENCES "paginas_imovel"("id") ON DELETE CASCADE,
	"tipo" varchar(20) NOT NULL,
	"origem" varchar(20) NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);
CREATE INDEX IF NOT EXISTS "paginas_imovel_eventos_pagina_idx" ON "paginas_imovel_eventos" ("pagina_id", "criado_em");
