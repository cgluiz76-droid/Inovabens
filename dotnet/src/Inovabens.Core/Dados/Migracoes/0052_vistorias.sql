-- Locação, fase 4 (docs/plano-locacao.md, decisão 6): vistoria terceirizada. O sistema registra a vistoria feita pela
-- empresa contratada: agendamento (agenda e chaves), laudo em PDF na pasta do contrato no Drive, leituras dos medidores,
-- custo, prazo de contestação/aceite do inquilino e as pendências apontadas no laudo, que viram chamado de manutenção
-- ou débito da rescisão. Sem laudo próprio com fotos.

CREATE SEQUENCE IF NOT EXISTS "vistorias_numero_seq";

CREATE TABLE "vistorias" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"numero" integer NOT NULL DEFAULT nextval('vistorias_numero_seq') UNIQUE,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"contrato_id" uuid NOT NULL REFERENCES "contratos_locacao"("id") ON DELETE CASCADE,
	"tipo" varchar(20) NOT NULL CHECK ("tipo" IN ('ENTRADA', 'SAIDA', 'PERIODICA')),
	"status" varchar(20) NOT NULL CHECK ("status" IN ('AGENDADA', 'REALIZADA', 'CANCELADA')),
	-- empresa de vistoria (Pessoa) e quem foi ao imóvel
	"empresa_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"vistoriador" varchar(200),
	"agendada_para" timestamp with time zone,
	"compromisso_id" uuid REFERENCES "compromissos"("id") ON DELETE SET NULL,
	"realizada_em" date,
	-- leituras dos medidores e chaves entregues/devolvidas no dia
	"leitura_agua" varchar(30),
	"leitura_luz" varchar(30),
	"leitura_gas" varchar(30),
	"chaves" smallint CHECK ("chaves" IS NULL OR "chaves" >= 0),
	-- laudo (PDF) na subpasta "Vistorias" da pasta do contrato
	"laudo_drive_id" varchar(200),
	"laudo_nome" varchar(250),
	-- prazo para o inquilino contestar o laudo; aceite ou contestação registrados pela equipe
	"prazo_contestacao" date,
	"aceite_em" date,
	"contestacao" varchar(2000),
	"contestada_em" date,
	-- custo da vistoria (mesmas formas de lançar da manutenção)
	"valor" numeric(14, 2) CHECK ("valor" IS NULL OR "valor" >= 0),
	"cobranca" varchar(20) CHECK ("cobranca" IS NULL OR "cobranca" IN ('REPASSE', 'FATURA', 'IMOVEL', 'IMOBILIARIA', 'DIRETO')),
	"vencimento_prestador" date,
	"lancamento_empresa_id" uuid REFERENCES "lancamentos_empresa"("id") ON DELETE SET NULL,
	"lancamento_imovel_id" uuid REFERENCES "lancamentos_financeiros"("id") ON DELETE SET NULL,
	"fatura_item_id" uuid REFERENCES "fatura_locacao_itens"("id") ON DELETE SET NULL,
	"observacao" varchar(2000),
	"motivo" varchar(1000),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS "ix_vistorias_contrato" ON "vistorias" ("contrato_id", "tipo");

-- pendências apontadas no laudo: viram chamado de manutenção (locador) ou débito da rescisão (locatário)
CREATE TABLE "vistoria_pendencias" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"vistoria_id" uuid NOT NULL REFERENCES "vistorias"("id") ON DELETE CASCADE,
	"comodo" varchar(100),
	"descricao" varchar(1000) NOT NULL,
	"responsavel" varchar(20) NOT NULL CHECK ("responsavel" IN ('LOCADOR', 'LOCATARIO', 'IMOBILIARIA')),
	"valor" numeric(14, 2) CHECK ("valor" IS NULL OR "valor" >= 0),
	"status" varchar(20) NOT NULL CHECK ("status" IN ('ABERTA', 'CHAMADO', 'COBRADA', 'RESOLVIDA')),
	"chamado_id" uuid REFERENCES "chamados_manutencao"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

-- custo da vistoria por conta do locador também sai do repasse (mesmo controle dos consertos)
ALTER TABLE "manutencao_abatimentos" ALTER COLUMN "chamado_id" DROP NOT NULL;
ALTER TABLE "manutencao_abatimentos" ADD COLUMN IF NOT EXISTS "vistoria_id" uuid REFERENCES "vistorias"("id") ON DELETE CASCADE;

INSERT INTO "categorias_empresa" ("nome", "tipo", "grupo") VALUES ('Vistoria de imóveis', 'SAIDA', 'DESPESA') ON CONFLICT DO NOTHING;
