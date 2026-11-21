-- Versão de 800 px das fotos públicas (ficha do site no celular: entre a de 400, borrada, e a de 1200, pesada).
-- Fotos publicadas antes desta migração não têm o arquivo -800.webp no R2: a rotina de publicação gera aos poucos
-- e marca aqui (publicada_media); enquanto false, o site não oferece a versão de 800.
ALTER TABLE "imovel_midias" ADD COLUMN IF NOT EXISTS "publicada_media" boolean NOT NULL DEFAULT false;
