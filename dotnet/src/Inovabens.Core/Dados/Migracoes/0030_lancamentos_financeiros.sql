-- Financeiro do imóvel reorganizado (2026-11):
--  * aba "Lançamentos Financeiros": receitas e despesas correntes com valor COM SINAL (receita +, despesa −),
--    nº sequencial por imóvel, conta orçamentária, situação (previsto → aberto → liquidado) e recorrência;
--  * aba "Investimento" (só administrador): aquisição (CAPEX), resumo dos lançamentos (OPEX), fechamento da
--    operação (venda) com o interruptor "incluir o saldo dos lançamentos" na apuração do lucro.
-- As tabelas antigas (despesas, despesa_lancamentos, receitas) estavam vazias em produção e são removidas.

-- 1) Contas orçamentárias (substituem as categorias de despesa), com tipo Receita/Despesa
CREATE TYPE "public"."tipo_conta" AS ENUM('RECEITA', 'DESPESA');
-- PREVISTO: gerado pela recorrência; ABERTO: título a pagar/receber; LIQUIDADO: pago/recebido (entra no saldo); IGNORADO: previsto descartado
CREATE TYPE "public"."situacao_lancamento" AS ENUM('PREVISTO', 'ABERTO', 'LIQUIDADO', 'IGNORADO');

ALTER TABLE "despesa_categorias" RENAME TO "contas_orcamentarias";
ALTER TABLE "contas_orcamentarias" RENAME CONSTRAINT "despesa_categorias_nome_unique" TO "contas_orcamentarias_nome_unique";
ALTER TABLE "contas_orcamentarias" ADD COLUMN "tipo" "tipo_conta" NOT NULL DEFAULT 'DESPESA';
INSERT INTO "contas_orcamentarias" ("nome", "tipo") VALUES
    ('Reembolso', 'RECEITA'),
    ('Taxa de administração de aluguel', 'RECEITA'),
    ('Aluguel recebido', 'RECEITA'),
    ('Outras receitas', 'RECEITA')
ON CONFLICT ("nome") DO NOTHING;

-- 2) Lançamentos financeiros
CREATE TABLE "lancamentos_financeiros" (
    "id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
    "imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
    -- nº sequencial no imóvel (exibido como CODIGO-0001); títulos ABERTOS e LIQUIDADOS têm número
    "numero" integer,
    "situacao" "situacao_lancamento" NOT NULL DEFAULT 'ABERTO',
    -- vencimento (data prevista do pagamento/recebimento)
    "data" date NOT NULL,
    "data_liquidacao" date,
    "valor" numeric(15, 2) NOT NULL,
    "descricao" varchar(200) NOT NULL,
    "conta_id" uuid NOT NULL REFERENCES "contas_orcamentarias"("id"),
    "observacao" text,
    "recorrente" boolean NOT NULL DEFAULT false,
    "dia_recorrencia" integer,
    "valor_estimado" numeric(15, 2),
    -- previsto gerado por um lançamento recorrente: origem + competência (dia 1 do mês)
    "origem_id" uuid REFERENCES "lancamentos_financeiros"("id") ON DELETE SET NULL,
    "competencia" date,
    "criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
    "atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
    "criado_em" timestamp with time zone DEFAULT now() NOT NULL,
    "atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
    "excluido_em" timestamp with time zone,
    CONSTRAINT "ck_lanc_valor" CHECK ("valor" <> 0),
    CONSTRAINT "ck_lanc_numero" CHECK (("situacao" IN ('ABERTO', 'LIQUIDADO')) = ("numero" IS NOT NULL) OR "excluido_em" IS NOT NULL),
    CONSTRAINT "ck_lanc_liquidacao" CHECK (("situacao" = 'LIQUIDADO') = ("data_liquidacao" IS NOT NULL)),
    CONSTRAINT "ck_lanc_recorrencia" CHECK (NOT "recorrente" OR ("dia_recorrencia" BETWEEN 1 AND 31 AND "valor_estimado" IS NOT NULL AND "valor_estimado" <> 0)),
    CONSTRAINT "ck_lanc_competencia" CHECK ("competencia" IS NULL OR extract(day from "competencia") = 1)
);
CREATE UNIQUE INDEX "ux_lanc_numero" ON "lancamentos_financeiros" ("imovel_id", "numero") WHERE "numero" IS NOT NULL;
CREATE UNIQUE INDEX "ux_lanc_origem_competencia" ON "lancamentos_financeiros" ("origem_id", "competencia") WHERE "origem_id" IS NOT NULL;
CREATE INDEX "ix_lanc_imovel_data" ON "lancamentos_financeiros" ("imovel_id", "data") WHERE "excluido_em" IS NULL;

-- 3) Comprovantes: passam a pertencer a um lançamento financeiro
DELETE FROM "financeiro_comprovantes";
ALTER TABLE "financeiro_comprovantes" DROP CONSTRAINT IF EXISTS "ck_comprovante_origem";
ALTER TABLE "financeiro_comprovantes" DROP COLUMN "receita_id";
ALTER TABLE "financeiro_comprovantes" DROP COLUMN "despesa_id";
ALTER TABLE "financeiro_comprovantes" DROP COLUMN "lancamento_id";
ALTER TABLE "financeiro_comprovantes" ADD COLUMN "lancamento_id" uuid NOT NULL REFERENCES "lancamentos_financeiros"("id") ON DELETE CASCADE;
CREATE INDEX "ix_comprovantes_lancamento" ON "financeiro_comprovantes" ("lancamento_id");

-- 4) Alertas de título em atraso apontam para a tabela nova
ALTER TYPE "public"."tipo_alerta" RENAME VALUE 'DESPESA_NAO_PAGA' TO 'TITULO_EM_ATRASO';
DELETE FROM "alertas" WHERE "lancamento_id" IS NOT NULL;
ALTER TABLE "alertas" DROP CONSTRAINT IF EXISTS "alertas_lancamento_id_despesa_lancamentos_id_fk";
ALTER TABLE "alertas" ADD CONSTRAINT "alertas_lancamento_id_fk" FOREIGN KEY ("lancamento_id") REFERENCES "lancamentos_financeiros"("id") ON DELETE CASCADE;

-- 5) Fechamento da operação (venda), um por imóvel
CREATE TABLE "fechamentos_investimento" (
    "imovel_id" uuid PRIMARY KEY NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
    "data" date NOT NULL,
    "valor_venda" numeric(15, 2) NOT NULL,
    "comissao" numeric(15, 2) DEFAULT 0 NOT NULL,
    "impostos" numeric(15, 2) DEFAULT 0 NOT NULL,
    "outros_custos" numeric(15, 2) DEFAULT 0 NOT NULL,
    "valor_liquido" numeric(15, 2) GENERATED ALWAYS AS ("valor_venda" - "comissao" - "impostos" - "outros_custos") STORED,
    -- NULL = ainda não decidido (a tela mostra os dois cenários)
    "incluir_lancamentos" boolean,
    "comprador_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
    "observacao" text,
    "atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
    "criado_em" timestamp with time zone DEFAULT now() NOT NULL,
    "atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT "ck_fech_valores" CHECK ("valor_venda" > 0 AND "comissao" >= 0 AND "impostos" >= 0 AND "outros_custos" >= 0)
);

-- 6) Estruturas antigas
DROP TABLE "despesa_lancamentos";
DROP TABLE "despesas";
DROP TABLE "receitas";
DROP TYPE IF EXISTS "public"."status_lancamento";
DROP TYPE IF EXISTS "public"."recorrencia";
DROP TYPE IF EXISTS "public"."tipo_receita";

-- 7) Permissão nova (aba Investimento): o perfil Administrador recebe todas pelo seed da aplicação
INSERT INTO "permissoes" ("codigo", "descricao") VALUES ('financeiro:investimento', 'financeiro:investimento') ON CONFLICT DO NOTHING;
INSERT INTO "perfil_permissoes" ("perfil_id", "permissao_id")
SELECT pf.id, pm.id FROM "perfis" pf JOIN "permissoes" pm ON pm.codigo = 'financeiro:investimento' WHERE pf.nome = 'Administrador'
ON CONFLICT DO NOTHING;
