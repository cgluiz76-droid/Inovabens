-- Cadastros mantidos pela Administração: tipos de imóvel e perfis de pessoa (antes fixos no código).

-- Tipos de imóvel. imoveis.tipo continua guardando o nome; a FK com ON UPDATE CASCADE faz a renomeação
-- de um tipo valer para todos os imóveis dele.
CREATE TABLE "tipos_imovel" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(40) NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL,
	CONSTRAINT "tipos_imovel_nome_unique" UNIQUE("nome")
);

INSERT INTO tipos_imovel (nome)
VALUES ('apartamento'), ('casa'), ('terreno'), ('sala comercial'), ('galpão'), ('loja'), ('fazenda/sítio'), ('outro')
ON CONFLICT DO NOTHING;
-- tipos digitados livremente nos imóveis já cadastrados
INSERT INTO tipos_imovel (nome) SELECT DISTINCT tipo FROM imoveis ON CONFLICT DO NOTHING;

ALTER TABLE "imoveis" ADD CONSTRAINT "imoveis_tipo_tipos_imovel_nome_fk"
	FOREIGN KEY ("tipo") REFERENCES "tipos_imovel"("nome") ON DELETE no action ON UPDATE cascade;

-- Perfis de pessoa (antigo enum papel_pessoa). O código é fixo; o nome exibido pode ser alterado.
-- CLIENTE (CRM) e PRESTADOR (especialidade) são usados pelo sistema.
CREATE TABLE "perfis_pessoa" (
	"codigo" varchar(40) PRIMARY KEY NOT NULL,
	"nome" varchar(60) NOT NULL,
	"ativo" boolean DEFAULT true NOT NULL,
	"ordem" integer DEFAULT 0 NOT NULL,
	CONSTRAINT "perfis_pessoa_nome_unique" UNIQUE("nome")
);

INSERT INTO perfis_pessoa (codigo, nome, ordem) VALUES
	('CLIENTE', 'Cliente', 1), ('PROPRIETARIO', 'Proprietário', 2), ('FIADOR', 'Fiador', 3),
	('PARCEIRO', 'Parceiro', 4), ('PRESTADOR', 'Prestador', 5), ('TERCEIRO', 'Terceiro', 6);

ALTER TABLE "pessoa_papeis" ALTER COLUMN "papel" TYPE varchar(40) USING "papel"::text;
ALTER TABLE "imovel_envolvidos" ALTER COLUMN "papel" TYPE varchar(40) USING "papel"::text;
ALTER TABLE "pessoa_papeis" ADD CONSTRAINT "pessoa_papeis_papel_perfis_pessoa_codigo_fk"
	FOREIGN KEY ("papel") REFERENCES "perfis_pessoa"("codigo") ON DELETE no action ON UPDATE no action;
ALTER TABLE "imovel_envolvidos" ADD CONSTRAINT "imovel_envolvidos_papel_perfis_pessoa_codigo_fk"
	FOREIGN KEY ("papel") REFERENCES "perfis_pessoa"("codigo") ON DELETE no action ON UPDATE no action;
DROP TYPE "papel_pessoa";
