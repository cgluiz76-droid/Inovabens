-- Cópia pública (Cloudflare R2, WebP em três tamanhos) das fotos marcadas como públicas, para o site e os portais.
-- Fotos não públicas ficam só no Google Drive.
ALTER TABLE "imovel_midias" ADD COLUMN "publica_chave" varchar(200);
ALTER TABLE "imovel_midias" ADD COLUMN "publicada_arquivo" varchar(100);
ALTER TABLE "imovel_midias" ADD COLUMN "publicada_em" timestamp with time zone;
ALTER TABLE "imovel_midias" ADD COLUMN "publicacao_erro" text;

-- Até aqui "publica" nascia verdadeira; agora a foto só vai para o site se o usuário marcar.
-- Documentos nunca são públicos; fotos anteriores ao Drive (subpasta nula) não têm original para publicar.
UPDATE "imovel_midias" SET "publica" = false WHERE "tipo" <> 'FOTO' OR "subpasta" IS NULL;
ALTER TABLE "imovel_midias" ALTER COLUMN "publica" SET DEFAULT false;
