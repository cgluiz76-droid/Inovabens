-- Fotos e documentos passam do S3 para o Google Drive (drive compartilhado da empresa).
-- Cada imóvel tem uma pasta principal, com o nome escolhido no cadastro, e seis subpastas fixas
-- (01 - Matrícula ... 06 - Protocolos). arquivo_chave e arquivo_original_chave passam a guardar o id do arquivo no Drive.
ALTER TABLE "imoveis" ADD COLUMN "nome_drive" varchar(120);
ALTER TABLE "imoveis" ADD COLUMN "drive_pasta_id" varchar(100);
CREATE UNIQUE INDEX "ux_imoveis_nome_drive" ON "imoveis" (lower("nome_drive"))
	WHERE "nome_drive" IS NOT NULL AND "excluido_em" IS NULL;

-- Número da subpasta (1 a 6) onde o arquivo foi guardado; null nos arquivos enviados antes do Drive.
ALTER TABLE "imovel_midias" ADD COLUMN "subpasta" smallint;
ALTER TABLE "imovel_midias" ADD CONSTRAINT "imovel_midias_subpasta_check" CHECK ("subpasta" BETWEEN 1 AND 6);
