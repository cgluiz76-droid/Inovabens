-- Financeiro da Imobiliária (menu lateral): as finanças da própria empresa, separadas do livro caixa de cada imóvel
-- (lancamentos_financeiros continua igual). Três peças:
--   contas_bancarias     onde o dinheiro está (conta corrente, poupança, aplicação, caixa) ou o que se deve (cartão);
--   categorias_empresa   plano de contas da imobiliária, cada categoria num grupo da DRE;
--   lancamentos_empresa  contas a pagar/receber e movimentos; transferência = dois lançamentos com o mesmo transferencia_id.
-- O aluguel passa pela conta da imobiliária: entra e sai em categorias do grupo TERCEIROS, que ficam FORA da DRE e
-- formam o "dinheiro de terceiros" (a repassar aos proprietários).
CREATE TABLE "contas_bancarias" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(80) NOT NULL,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('CORRENTE', 'POUPANCA', 'APLICACAO', 'CAIXA', 'CARTAO_CREDITO')),
	"banco" varchar(60),
	"agencia" varchar(20),
	"numero" varchar(30),
	-- saldo no início do dia data_saldo_inicial; lançamentos pagos antes dessa data não entram no saldo
	"saldo_inicial" numeric(15,2) DEFAULT 0 NOT NULL,
	"data_saldo_inicial" date NOT NULL,
	-- só cartão de crédito: dia em que a fatura fecha e dia em que vence
	"dia_fechamento" smallint CHECK ("dia_fechamento" BETWEEN 1 AND 31),
	"dia_vencimento" smallint CHECK ("dia_vencimento" BETWEEN 1 AND 31),
	"limite" numeric(15,2),
	"ativa" boolean DEFAULT true NOT NULL,
	"ordem" integer DEFAULT 0 NOT NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE TABLE "categorias_empresa" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"nome" varchar(80) NOT NULL,
	-- ENTRADA ou SAIDA: filtra a lista no formulário e dá o sinal do valor
	"tipo" varchar(10) NOT NULL CHECK ("tipo" IN ('ENTRADA', 'SAIDA')),
	-- linha da DRE: RECEITA, DEDUCAO, CUSTO, DESPESA, FINANCEIRO; fora do resultado: TERCEIROS, PATRIMONIAL
	"grupo" varchar(20) NOT NULL CHECK ("grupo" IN ('RECEITA', 'DEDUCAO', 'CUSTO', 'DESPESA', 'FINANCEIRO', 'TERCEIROS', 'PATRIMONIAL')),
	"ativa" boolean DEFAULT true NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "ux_categorias_empresa_nome" ON "categorias_empresa" (upper("nome"));

CREATE TABLE "lancamentos_empresa" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"descricao" varchar(200) NOT NULL,
	-- com sinal: entrada +, saída −
	"valor" numeric(15,2) NOT NULL CHECK ("valor" <> 0),
	"vencimento" date NOT NULL,
	-- preenchida = pago/recebido (entra no saldo da conta nessa data); vazia = em aberto
	"data_pagamento" date,
	-- mês de competência (sempre dia 1): base da DRE
	"competencia" date NOT NULL CHECK (extract(day FROM "competencia") = 1),
	-- vazia só em transferência
	"categoria_id" uuid REFERENCES "categorias_empresa"("id"),
	-- obrigatória quando pago; em aberto pode ficar sem
	"conta_id" uuid REFERENCES "contas_bancarias"("id"),
	"transferencia_id" uuid,
	"pessoa_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"pessoa_nome" varchar(200),
	-- aluguel recebido / repasse: de qual imóvel
	"imovel_id" uuid REFERENCES "imoveis"("id") ON DELETE SET NULL,
	"documento" varchar(60),
	"observacao" varchar(1000),
	-- compra parcelada: "3/10"
	"parcela" varchar(10),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"excluido_em" timestamp with time zone,
	CONSTRAINT "ck_lancamento_empresa_categoria" CHECK ("categoria_id" IS NOT NULL OR "transferencia_id" IS NOT NULL),
	CONSTRAINT "ck_lancamento_empresa_pago_com_conta" CHECK ("data_pagamento" IS NULL OR "conta_id" IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS "ix_lancamentos_empresa_vencimento" ON "lancamentos_empresa" ("vencimento") WHERE "excluido_em" IS NULL;
CREATE INDEX IF NOT EXISTS "ix_lancamentos_empresa_conta" ON "lancamentos_empresa" ("conta_id", "data_pagamento") WHERE "excluido_em" IS NULL;
CREATE INDEX IF NOT EXISTS "ix_lancamentos_empresa_competencia" ON "lancamentos_empresa" ("competencia") WHERE "excluido_em" IS NULL;
CREATE INDEX IF NOT EXISTS "ix_lancamentos_empresa_transferencia" ON "lancamentos_empresa" ("transferencia_id") WHERE "transferencia_id" IS NOT NULL;

-- Plano de contas inicial de uma imobiliária (editável na tela: renomear, inativar, criar)
INSERT INTO "categorias_empresa" ("nome", "tipo", "grupo") VALUES
	('Comissão de venda', 'ENTRADA', 'RECEITA'),
	('Taxa de administração de aluguel', 'ENTRADA', 'RECEITA'),
	('Taxa de intermediação de locação', 'ENTRADA', 'RECEITA'),
	('Avaliações e pareceres', 'ENTRADA', 'RECEITA'),
	('Outras receitas', 'ENTRADA', 'RECEITA'),
	('Impostos sobre o faturamento (Simples/ISS)', 'SAIDA', 'DEDUCAO'),
	('Comissões pagas a corretores e parceiros', 'SAIDA', 'CUSTO'),
	('Salários e encargos', 'SAIDA', 'DESPESA'),
	('Pró-labore', 'SAIDA', 'DESPESA'),
	('Aluguel e condomínio do escritório', 'SAIDA', 'DESPESA'),
	('Energia, água, telefone e internet', 'SAIDA', 'DESPESA'),
	('Marketing e portais imobiliários', 'SAIDA', 'DESPESA'),
	('Sistemas e assinaturas', 'SAIDA', 'DESPESA'),
	('Contabilidade e jurídico', 'SAIDA', 'DESPESA'),
	('CRECI e anuidades', 'SAIDA', 'DESPESA'),
	('Combustível e deslocamento', 'SAIDA', 'DESPESA'),
	('Material de escritório', 'SAIDA', 'DESPESA'),
	('Outras despesas', 'SAIDA', 'DESPESA'),
	('Rendimentos de aplicação', 'ENTRADA', 'FINANCEIRO'),
	('Tarifas bancárias', 'SAIDA', 'FINANCEIRO'),
	('Juros e multas pagos', 'SAIDA', 'FINANCEIRO'),
	('Aluguel recebido de inquilino', 'ENTRADA', 'TERCEIROS'),
	('Repasse ao proprietário', 'SAIDA', 'TERCEIROS'),
	('Caução recebida', 'ENTRADA', 'TERCEIROS'),
	('Caução devolvida', 'SAIDA', 'TERCEIROS'),
	('Despesa paga por conta do proprietário', 'SAIDA', 'TERCEIROS'),
	('Aporte dos sócios', 'ENTRADA', 'PATRIMONIAL'),
	('Distribuição de lucros', 'SAIDA', 'PATRIMONIAL'),
	('Empréstimo recebido', 'ENTRADA', 'PATRIMONIAL'),
	('Pagamento de empréstimo', 'SAIDA', 'PATRIMONIAL'),
	('Compra de equipamentos e móveis', 'SAIDA', 'PATRIMONIAL')
ON CONFLICT DO NOTHING;
