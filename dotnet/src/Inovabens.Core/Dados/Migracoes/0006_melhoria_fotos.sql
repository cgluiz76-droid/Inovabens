-- Melhoria automática de fotos pelo Magnific (upscaler Precision V2). A foto melhorada passa a ser o arquivo
-- exibido; a enviada pelo usuário fica em arquivo_original_chave para poder ser restaurada.
ALTER TABLE "imovel_midias" ADD COLUMN "arquivo_original_chave" text;
ALTER TABLE "imovel_midias" ADD COLUMN "melhoria_status" varchar(20);
ALTER TABLE "imovel_midias" ADD COLUMN "melhoria_tarefa" varchar(64);
ALTER TABLE "imovel_midias" ADD COLUMN "melhoria_erro" text;
ALTER TABLE "imovel_midias" ADD COLUMN "melhoria_atualizada_em" timestamp with time zone;
ALTER TABLE "imovel_midias" ADD CONSTRAINT "imovel_midias_melhoria_status_check"
	CHECK ("melhoria_status" IN ('PENDENTE', 'PROCESSANDO', 'CONCLUIDA', 'FALHA'));
CREATE INDEX "ix_midias_melhoria_aberta" ON "imovel_midias" ("melhoria_atualizada_em")
	WHERE "melhoria_status" IN ('PENDENTE', 'PROCESSANDO');
