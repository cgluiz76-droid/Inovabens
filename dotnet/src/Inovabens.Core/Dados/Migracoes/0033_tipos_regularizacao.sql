-- Tipos de regularização (aba Regularização do imóvel) viram cadastro mantido na Administração → Cadastros,
-- no lugar do enum fixo. regularizacoes.tipo guarda o nome; a FK com ON UPDATE CASCADE faz a renomeação valer
-- para as regularizações já lançadas (mesmo modelo de tipos_imovel).
CREATE TABLE "tipos_regularizacao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(80) NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL,
	CONSTRAINT "tipos_regularizacao_nome_unique" UNIQUE("nome")
);

-- os antigos valores do enum, já com o nome exibido, e os tipos de documento pedidos pela equipe
INSERT INTO tipos_regularizacao (nome) VALUES
	('Documentação'), ('Tributos em atraso'), ('Desmembramento'), ('Fracionamento'), ('Retificação de área'), ('Outro'),
	('Escritura'), ('Instrumento Particular'), ('Registro')
ON CONFLICT DO NOTHING;

DO $$
BEGIN
	IF (SELECT data_type FROM information_schema.columns WHERE table_name = 'regularizacoes' AND column_name = 'tipo') = 'USER-DEFINED' THEN
		ALTER TABLE "regularizacoes" ALTER COLUMN "tipo" TYPE varchar(80) USING (CASE "tipo"::text
			WHEN 'DOCUMENTACAO' THEN 'Documentação'
			WHEN 'TRIBUTOS_ATRASO' THEN 'Tributos em atraso'
			WHEN 'DESMEMBRAMENTO' THEN 'Desmembramento'
			WHEN 'FRACIONAMENTO' THEN 'Fracionamento'
			WHEN 'RETIFICACAO_AREA' THEN 'Retificação de área'
			ELSE 'Outro' END);
		ALTER TABLE "regularizacoes" ADD CONSTRAINT "regularizacoes_tipo_tipos_regularizacao_nome_fk"
			FOREIGN KEY ("tipo") REFERENCES "tipos_regularizacao"("nome") ON DELETE no action ON UPDATE cascade;
	END IF;
END $$;

DROP TYPE IF EXISTS "public"."tipo_regularizacao";
