-- Locação, fases 1 e 2 (docs/plano-locacao.md): contrato, partes, faturas mensais, pagamentos e repasses.
-- O imóvel continua com a situação comercial dele (à venda, disponível...); "locado" vem do contrato ATIVO, assim o
-- mesmo imóvel pode estar locado e à venda. Tipos gravados como texto com CHECK (não enums do PostgreSQL).

-- perfil de pessoa usado pelo contrato (proprietário, fiador e prestador já existem)
INSERT INTO "perfis_pessoa" ("codigo", "nome", "ordem") VALUES ('LOCATARIO', 'Locatário', 7) ON CONFLICT DO NOTHING;

CREATE SEQUENCE IF NOT EXISTS "contratos_locacao_numero_seq";

CREATE TABLE "contratos_locacao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	-- exibido como LOC-0001; nunca se repete
	"numero" integer NOT NULL DEFAULT nextval('contratos_locacao_numero_seq') UNIQUE,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id"),
	-- ADMINISTRACAO: imóvel de terceiros (taxa, repasse, DIMOB); PROPRIO: imóvel da empresa ou de sócios (sem repasse)
	"modalidade" varchar(20) NOT NULL DEFAULT 'ADMINISTRACAO' CHECK ("modalidade" IN ('ADMINISTRACAO', 'PROPRIO')),
	"tipo" varchar(20) NOT NULL DEFAULT 'RESIDENCIAL' CHECK ("tipo" IN ('RESIDENCIAL', 'COMERCIAL')),
	"status" varchar(20) NOT NULL DEFAULT 'RASCUNHO' CHECK ("status" IN ('RASCUNHO', 'ATIVO', 'ENCERRADO', 'CANCELADO')),
	"proposta_id" uuid REFERENCES "propostas"("id") ON DELETE SET NULL,
	"inicio" date NOT NULL,
	"prazo_meses" integer NOT NULL CHECK ("prazo_meses" BETWEEN 1 AND 600),
	"dia_vencimento" smallint NOT NULL CHECK ("dia_vencimento" BETWEEN 1 AND 28),
	-- false: o aluguel do mês vence no mês seguinte (regra da Lei 8.245, art. 20); true: só sem garantia ou temporada
	"pagamento_antecipado" boolean NOT NULL DEFAULT false,
	"valor_aluguel" numeric(14, 2) NOT NULL CHECK ("valor_aluguel" > 0),
	-- encargos mensais cobrados junto com o aluguel e repassados ao proprietário
	"valor_condominio" numeric(14, 2) NOT NULL DEFAULT 0 CHECK ("valor_condominio" >= 0),
	"valor_iptu" numeric(14, 2) NOT NULL DEFAULT 0 CHECK ("valor_iptu" >= 0),
	"valor_seguro_incendio" numeric(14, 2) NOT NULL DEFAULT 0 CHECK ("valor_seguro_incendio" >= 0),
	"indice_reajuste" "codigo_indice" NOT NULL DEFAULT 'IGPM',
	"garantia" "garantia_locacao" NOT NULL DEFAULT 'SEM_GARANTIA',
	"garantia_valor" numeric(14, 2),
	"garantia_detalhe" varchar(500),
	-- padrões da Inova Bens: 10% de administração, intermediação = 1º aluguel, multa 10% + juros 1% a.m.
	"taxa_administracao_pct" numeric(5, 2) NOT NULL DEFAULT 10 CHECK ("taxa_administracao_pct" BETWEEN 0 AND 100),
	"intermediacao_valor" numeric(14, 2) NOT NULL DEFAULT 0 CHECK ("intermediacao_valor" >= 0),
	"multa_atraso_pct" numeric(5, 2) NOT NULL DEFAULT 10 CHECK ("multa_atraso_pct" BETWEEN 0 AND 20),
	"juros_mes_pct" numeric(5, 2) NOT NULL DEFAULT 1 CHECK ("juros_mes_pct" BETWEEN 0 AND 10),
	"multa_rescisoria_alugueis" numeric(4, 1) NOT NULL DEFAULT 3 CHECK ("multa_rescisoria_alugueis" BETWEEN 0 AND 12),
	"dias_repasse" smallint NOT NULL DEFAULT 5 CHECK ("dias_repasse" BETWEEN 0 AND 30),
	-- venda do imóvel locado (arts. 8 e 27): com as duas, o comprador respeita o contrato
	"clausula_vigencia" boolean NOT NULL DEFAULT false,
	"averbado" boolean NOT NULL DEFAULT false,
	"observacoes" text,
	"ativado_em" timestamp with time zone,
	"encerrado_em" date,
	"motivo_encerramento" varchar(500),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);

-- um imóvel só tem um contrato ATIVO por vez
CREATE UNIQUE INDEX IF NOT EXISTS "ux_contratos_locacao_ativo" ON "contratos_locacao" ("imovel_id") WHERE "status" = 'ATIVO';
CREATE INDEX IF NOT EXISTS "ix_contratos_locacao_imovel" ON "contratos_locacao" ("imovel_id");

CREATE TABLE "contrato_locacao_partes" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"contrato_id" uuid NOT NULL REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	"pessoa_id" uuid NOT NULL REFERENCES "pessoas"("id"),
	"papel" varchar(20) NOT NULL CHECK ("papel" IN ('LOCADOR', 'LOCATARIO', 'FIADOR')),
	-- locadores: parte de cada um no repasse (soma 100)
	"percentual" numeric(5, 2) CHECK ("percentual" IS NULL OR "percentual" BETWEEN 0 AND 100),
	CONSTRAINT "ux_contrato_locacao_parte" UNIQUE ("contrato_id", "pessoa_id", "papel")
);

CREATE TABLE "faturas_locacao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"contrato_id" uuid NOT NULL REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	-- mês de uso do imóvel (dia 1)
	"competencia" date NOT NULL,
	"vencimento" date NOT NULL,
	"status" varchar(20) NOT NULL DEFAULT 'ABERTA' CHECK ("status" IN ('ABERTA', 'PAGA', 'CANCELADA')),
	"observacao" varchar(500),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"cancelada_em" timestamp with time zone,
	"motivo_cancelamento" varchar(300)
);

-- uma fatura válida por mês de cada contrato (cancelada pode ser refeita)
CREATE UNIQUE INDEX IF NOT EXISTS "ux_faturas_locacao_competencia" ON "faturas_locacao" ("contrato_id", "competencia") WHERE "status" <> 'CANCELADA';
CREATE INDEX IF NOT EXISTS "ix_faturas_locacao_vencimento" ON "faturas_locacao" ("vencimento") WHERE "status" = 'ABERTA';

CREATE TABLE "fatura_locacao_itens" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"fatura_id" uuid NOT NULL REFERENCES "faturas_locacao"("id") ON DELETE CASCADE,
	"ordem" integer NOT NULL DEFAULT 0,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('ALUGUEL', 'CONDOMINIO', 'IPTU', 'SEGURO_INCENDIO', 'DESCONTO', 'OUTRO')),
	"descricao" varchar(200) NOT NULL,
	-- DESCONTO é negativo; os demais positivos
	"valor" numeric(14, 2) NOT NULL
);

-- cada recebimento (pode haver pagamento parcial); multa e juros calculados pela data e guardados aqui
CREATE SEQUENCE IF NOT EXISTS "recibos_locacao_numero_seq";

CREATE TABLE "fatura_locacao_pagamentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"fatura_id" uuid NOT NULL REFERENCES "faturas_locacao"("id") ON DELETE CASCADE,
	-- número do recibo (art. 22, VI: recibo discriminado)
	"recibo_numero" integer NOT NULL DEFAULT nextval('recibos_locacao_numero_seq') UNIQUE,
	"data" date NOT NULL,
	-- principal abatido da fatura (sem multa e juros)
	"principal" numeric(14, 2) NOT NULL CHECK ("principal" > 0),
	"multa" numeric(14, 2) NOT NULL DEFAULT 0 CHECK ("multa" >= 0),
	"juros" numeric(14, 2) NOT NULL DEFAULT 0 CHECK ("juros" >= 0),
	"forma" varchar(30),
	"conta_bancaria_id" uuid REFERENCES "contas_bancarias"("id"),
	"observacao" varchar(300),
	"registrado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"registrado_em" timestamp with time zone DEFAULT now() NOT NULL,
	-- lançamentos criados no financeiro (imobiliária ou imóvel próprio), desfeitos junto com o estorno
	"lancamentos_empresa_ids" uuid[] NOT NULL DEFAULT '{}',
	"lancamento_imovel_id" uuid,
	"estornado_em" timestamp with time zone,
	"estornado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS "ix_fatura_locacao_pagamentos_fatura" ON "fatura_locacao_pagamentos" ("fatura_id");

-- quanto vai para cada locador a partir de um recebimento (modalidade ADMINISTRACAO)
CREATE TABLE "repasses_locacao" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"pagamento_id" uuid NOT NULL REFERENCES "fatura_locacao_pagamentos"("id") ON DELETE CASCADE,
	"contrato_id" uuid NOT NULL REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	"pessoa_id" uuid NOT NULL REFERENCES "pessoas"("id"),
	"percentual" numeric(5, 2) NOT NULL,
	-- parte do locador no recebido (aluguel, encargos, multa e juros)
	"bruto" numeric(14, 2) NOT NULL,
	"taxa_administracao" numeric(14, 2) NOT NULL DEFAULT 0,
	"intermediacao" numeric(14, 2) NOT NULL DEFAULT 0,
	"liquido" numeric(14, 2) NOT NULL,
	"previsto_para" date NOT NULL,
	"pago_em" date,
	"lancamento_empresa_id" uuid REFERENCES "lancamentos_empresa"("id") ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS "ix_repasses_locacao_contrato" ON "repasses_locacao" ("contrato_id");
CREATE INDEX IF NOT EXISTS "ix_repasses_locacao_aberto" ON "repasses_locacao" ("previsto_para") WHERE "pago_em" IS NULL;
