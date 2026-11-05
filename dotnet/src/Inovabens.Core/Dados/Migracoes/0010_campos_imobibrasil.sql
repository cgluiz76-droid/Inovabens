-- Campos que o cadastro do Imobibrasil (site e portais atuais) tem e o sistema ainda não tinha.
-- Também usados no site novo e nos feeds dos portais (ZAP, OLX, ImovelWeb).

-- Código de referência dos anúncios (ex.: CA0060, TE0042) e id do anúncio no Imobibrasil (evita importar duas vezes).
ALTER TABLE "imoveis" ADD COLUMN "referencia" varchar(20);
ALTER TABLE "imoveis" ADD COLUMN "imobibrasil_id" bigint;
CREATE UNIQUE INDEX "imoveis_referencia_unica" ON "imoveis" (lower("referencia")) WHERE "referencia" IS NOT NULL AND "excluido_em" IS NULL;
CREATE UNIQUE INDEX "imoveis_imobibrasil_unico" ON "imoveis" ("imobibrasil_id") WHERE "imobibrasil_id" IS NOT NULL AND "excluido_em" IS NULL;

-- Terreno: área própria (a área total pode somar construção e terreno) e medidas das divisas, em metros.
ALTER TABLE "imoveis" ADD COLUMN "area_terreno_m2" numeric(12,2) CHECK ("area_terreno_m2" >= 0);
ALTER TABLE "imoveis" ADD COLUMN "terreno_frente_m" numeric(10,2) CHECK ("terreno_frente_m" >= 0);
ALTER TABLE "imoveis" ADD COLUMN "terreno_fundo_m" numeric(10,2) CHECK ("terreno_fundo_m" >= 0);
ALTER TABLE "imoveis" ADD COLUMN "terreno_esquerda_m" numeric(10,2) CHECK ("terreno_esquerda_m" >= 0);
ALTER TABLE "imoveis" ADD COLUMN "terreno_direita_m" numeric(10,2) CHECK ("terreno_direita_m" >= 0);

-- Localização complementar.
ALTER TABLE "imoveis" ADD COLUMN "zona" varchar(60);
ALTER TABLE "imoveis" ADD COLUMN "regiao" varchar(100);
ALTER TABLE "imoveis" ADD COLUMN "ponto_referencia" varchar(200);

-- Construção e cômodos.
ALTER TABLE "imoveis" ADD COLUMN "pavimento" varchar(40);
ALTER TABLE "imoveis" ADD COLUMN "ano_construcao" integer CHECK ("ano_construcao" BETWEEN 1800 AND 2100);
ALTER TABLE "imoveis" ADD COLUMN "salas" integer CHECK ("salas" >= 0);

-- Negócio e custos mensais.
ALTER TABLE "imoveis" ADD COLUMN "aceita_permuta" boolean NOT NULL DEFAULT false;
ALTER TABLE "imoveis" ADD COLUMN "em_condominio" boolean NOT NULL DEFAULT false;
ALTER TABLE "imoveis" ADD COLUMN "valor_condominio" numeric(15,2) CHECK ("valor_condominio" >= 0);
ALTER TABLE "imoveis" ADD COLUMN "valor_iptu" numeric(15,2) CHECK ("valor_iptu" >= 0);

-- Vídeo do anúncio no YouTube (os vídeos enviados ficam no Drive, em "04 - Fotos e Vídeos").
ALTER TABLE "imoveis" ADD COLUMN "video_url" varchar(300);

-- Características agrupadas como no Imobibrasil: Características, Cômodos, Proximidades.
ALTER TABLE "imovel_caracteristicas" ADD COLUMN "grupo" varchar(40);

-- Tipos usados no Imobibrasil que faltavam (Barracão vira galpão; Sítio, fazenda/sítio).
INSERT INTO "tipos_imovel" ("nome") VALUES ('área'), ('casa em condomínio'), ('comercial')
ON CONFLICT DO NOTHING;
