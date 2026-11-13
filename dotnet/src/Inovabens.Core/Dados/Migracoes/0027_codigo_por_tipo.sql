-- Código do imóvel no padrão do Imobibrasil: prefixo do tipo + sequencial de 4 dígitos (AR0001, CA0108...).
-- O sequencial continua do maior código já usado no Imobibrasil (ativos, inativos e vendidos, levantado no painel
-- em 2026-11-13) e do maior código já gravado no sistema. O código IMV-... antigo fica em codigo_antigo (busca e
-- eventos antigos da agenda com #IMV-000012).

ALTER TABLE "tipos_imovel" ADD COLUMN "prefixo_codigo" varchar(2);
UPDATE "tipos_imovel" SET "prefixo_codigo" = CASE lower("nome")
	WHEN 'apartamento' THEN 'AP'
	WHEN 'área' THEN 'AR'
	WHEN 'galpão' THEN 'BA'
	WHEN 'casa' THEN 'CA'
	WHEN 'casa em condomínio' THEN 'CA'
	WHEN 'chácara' THEN 'CH'
	WHEN 'comercial' THEN 'CO'
	WHEN 'loja' THEN 'LO'
	WHEN 'sala comercial' THEN 'SA'
	WHEN 'fazenda/sítio' THEN 'SI'
	WHEN 'sobrado' THEN 'SO'
	WHEN 'terreno' THEN 'TE'
	ELSE upper(left(translate(lower("nome"), 'áàâãéêíóôõúçü', 'aaaaeeiooouuc'), 2))
END;
ALTER TABLE "tipos_imovel" ALTER COLUMN "prefixo_codigo" SET NOT NULL;
ALTER TABLE "tipos_imovel" ADD CONSTRAINT "tipos_imovel_prefixo_codigo_check" CHECK ("prefixo_codigo" ~ '^[A-Z]{2}$');

-- último número já usado por prefixo fora do sistema (Imobibrasil)
CREATE TABLE "imoveis_codigo_sequencia" (
	"prefixo" varchar(2) PRIMARY KEY NOT NULL CHECK ("prefixo" ~ '^[A-Z]{2}$'),
	"ultimo" integer NOT NULL CHECK ("ultimo" >= 0)
);
INSERT INTO "imoveis_codigo_sequencia" ("prefixo", "ultimo") VALUES
	('AD', 1), ('AP', 37), ('AR', 7), ('BA', 9), ('CA', 108), ('CH', 6), ('CO', 2),
	('RA', 1), ('SA', 5), ('SI', 2), ('SO', 3), ('TE', 44);

ALTER TABLE "imoveis" ADD COLUMN "codigo_antigo" varchar(20);
-- pasta do Drive que precisa ganhar o nome novo (feito pela aplicação, fora da migração)
ALTER TABLE "imoveis" ADD COLUMN "drive_renomear" boolean DEFAULT false NOT NULL;
CREATE INDEX "ix_imoveis_codigo_antigo" ON "imoveis" (upper("codigo_antigo")) WHERE "codigo_antigo" IS NOT NULL;

-- 1) quem já tem a referência do Imobibrasil passa a usá-la como código
UPDATE "imoveis" SET "codigo_antigo" = "codigo", "codigo" = upper("referencia")
WHERE "excluido_em" IS NULL AND "referencia" ~* '^[A-Z]{2}[0-9]{4}$' AND "codigo" !~ '^[A-Z]{2}[0-9]{4}$'
	AND NOT EXISTS (SELECT 1 FROM "imoveis" o WHERE o."codigo" = upper("imoveis"."referencia"));

-- 2) os demais ganham o próximo código do prefixo do tipo, na ordem em que foram cadastrados
DO $$
DECLARE r record; n integer;
BEGIN
	FOR r IN
		SELECT i."id", t."prefixo_codigo" AS p FROM "imoveis" i JOIN "tipos_imovel" t ON t."nome" = i."tipo"
		WHERE i."excluido_em" IS NULL AND i."codigo" !~ '^[A-Z]{2}[0-9]{4}$' ORDER BY i."criado_em", i."codigo"
	LOOP
		n := greatest(
			coalesce((SELECT s."ultimo" FROM "imoveis_codigo_sequencia" s WHERE s."prefixo" = r.p), 0),
			coalesce((SELECT max(substring(c."codigo" FROM 3)::int) FROM "imoveis" c WHERE c."codigo" ~ ('^' || r.p || '[0-9]{4}$')), 0)
		) + 1;
		UPDATE "imoveis" SET "codigo_antigo" = "codigo", "codigo" = r.p || lpad(n::text, 4, '0') WHERE "id" = r."id";
	END LOOP;
END $$;

-- 3) a referência dos portais é o próprio código
UPDATE "imoveis" SET "referencia" = "codigo" WHERE "excluido_em" IS NULL AND "codigo" ~ '^[A-Z]{2}[0-9]{4}$';

-- 4) pasta do Drive começa pelo código: "CA0060 - Casa São Francisco"
UPDATE "imoveis"
SET "nome_drive" = left("codigo" || ' - ' || regexp_replace("nome_drive", '^([A-Z]{2}[0-9]{4}|IMV-[0-9]+)\s*-?\s*', ''), 120),
	"drive_renomear" = "drive_pasta_id" IS NOT NULL
WHERE "excluido_em" IS NULL AND "nome_drive" IS NOT NULL AND "nome_drive" NOT LIKE "codigo" || ' - %';
