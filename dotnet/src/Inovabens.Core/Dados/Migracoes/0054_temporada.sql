-- Locação, fase 8: temporada (Lei 8.245, arts. 48 a 50: até 90 dias, aluguel pode ser cobrado antecipado).
-- Reservas por diária com calendário por imóvel, limpeza, caução, check-in/out, repasse por reserva, calendário
-- sincronizado com Airbnb/Booking por iCal e pedido de reserva pelo site.

-- configuração do imóvel de temporada (um por imóvel)
CREATE TABLE "temporada_imoveis" (
	"imovel_id" uuid PRIMARY KEY REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"ativo" boolean DEFAULT true NOT NULL,
	-- ADMINISTRACAO: de terceiros (comissão e repasse); PROPRIO: da empresa ou de sócios (receita no financeiro do imóvel)
	"modalidade" varchar(20) DEFAULT 'ADMINISTRACAO' NOT NULL CHECK ("modalidade" IN ('ADMINISTRACAO', 'PROPRIO')),
	"diaria" numeric(14, 2) NOT NULL CHECK ("diaria" > 0),
	-- noites de sexta e sábado (vazio = diária normal)
	"diaria_fim_semana" numeric(14, 2) CHECK ("diaria_fim_semana" > 0),
	"minimo_noites" smallint DEFAULT 2 NOT NULL CHECK ("minimo_noites" BETWEEN 1 AND 90),
	"hospedes_max" smallint DEFAULT 4 NOT NULL CHECK ("hospedes_max" BETWEEN 1 AND 50),
	"taxa_limpeza" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("taxa_limpeza" >= 0),
	"caucao" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("caucao" >= 0),
	-- comissão da imobiliária sobre as diárias
	"comissao_pct" numeric(5, 2) DEFAULT 20 NOT NULL CHECK ("comissao_pct" BETWEEN 0 AND 100),
	"checkin" varchar(5) DEFAULT '14:00' NOT NULL,
	"checkout" varchar(5) DEFAULT '11:00' NOT NULL,
	"regras" varchar(2000),
	-- quem faz a limpeza (recebe a taxa de limpeza)
	"faxineira_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	-- link secreto do calendário exportado (.ics) para o Airbnb/Booking
	"ical_token" varchar(64) NOT NULL UNIQUE,
	"atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL
);

-- calendários externos importados (link .ics do Airbnb, Booking...)
CREATE TABLE "temporada_calendarios" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "temporada_imoveis"("imovel_id") ON DELETE CASCADE,
	"origem" varchar(20) NOT NULL CHECK ("origem" IN ('AIRBNB', 'BOOKING', 'OUTRA')),
	"url" varchar(1000) NOT NULL,
	"ultima_sincronizacao" timestamp with time zone,
	"erro" varchar(500),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL
);

CREATE INDEX IF NOT EXISTS "ix_temporada_calendarios_imovel" ON "temporada_calendarios" ("imovel_id");

CREATE SEQUENCE IF NOT EXISTS "temporada_reservas_numero_seq";

CREATE TABLE "temporada_reservas" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"numero" integer NOT NULL DEFAULT nextval('temporada_reservas_numero_seq') UNIQUE,
	"imovel_id" uuid NOT NULL REFERENCES "temporada_imoveis"("imovel_id") ON DELETE RESTRICT,
	"origem" varchar(20) NOT NULL CHECK ("origem" IN ('DIRETA', 'SITE', 'AIRBNB', 'BOOKING', 'OUTRA')),
	-- PEDIDO não bloqueia datas; CONFIRMADA, EM_CURSO, FINALIZADA e FECHADA bloqueiam
	"status" varchar(20) NOT NULL CHECK ("status" IN ('PEDIDO', 'CONFIRMADA', 'EM_CURSO', 'FINALIZADA', 'FECHADA', 'CANCELADA')),
	"hospede_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"hospede_nome" varchar(200) NOT NULL,
	"hospede_telefone" varchar(40),
	"hospede_email" varchar(200),
	"hospede_documento" varchar(20),
	"hospedes" smallint DEFAULT 1 NOT NULL CHECK ("hospedes" BETWEEN 1 AND 50),
	"entrada" date NOT NULL,
	"saida" date NOT NULL,
	"diarias" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("diarias" >= 0),
	"desconto" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("desconto" >= 0),
	"limpeza" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("limpeza" >= 0),
	"caucao" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("caucao" >= 0),
	-- reserva de plataforma: taxa que a plataforma retém (sai do repasse do proprietário)
	"taxa_plataforma" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("taxa_plataforma" >= 0),
	"comissao_pct" numeric(5, 2) NOT NULL CHECK ("comissao_pct" BETWEEN 0 AND 100),
	-- caução: quanto ficou retido (danos) no check-out; o resto volta ao hóspede
	"caucao_retida" numeric(14, 2) DEFAULT 0 NOT NULL CHECK ("caucao_retida" >= 0),
	"observacoes" varchar(2000),
	-- importada de calendário externo: id do evento (UID do iCal); valores lançados à mão depois
	"calendario_id" uuid REFERENCES "temporada_calendarios"("id") ON DELETE SET NULL,
	"externo_uid" varchar(300),
	"checkin_em" timestamp with time zone,
	"checkout_em" timestamp with time zone,
	"fechada_em" timestamp with time zone,
	"lancamentos_empresa_ids" uuid[] DEFAULT '{}' NOT NULL,
	"cancelada_em" timestamp with time zone,
	"motivo_cancelamento" varchar(500),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CHECK ("saida" > "entrada"),
	CHECK ("saida" - "entrada" <= 90)
);

CREATE INDEX IF NOT EXISTS "ix_temporada_reservas_imovel" ON "temporada_reservas" ("imovel_id", "entrada");
CREATE UNIQUE INDEX IF NOT EXISTS "ux_temporada_reservas_externo" ON "temporada_reservas" ("calendario_id", "externo_uid") WHERE "externo_uid" IS NOT NULL;

-- recebimentos do hóspede (sinal, saldo, caução); cada um lança no financeiro
CREATE TABLE "temporada_pagamentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"reserva_id" uuid NOT NULL REFERENCES "temporada_reservas"("id") ON DELETE CASCADE,
	"data" date NOT NULL,
	"valor" numeric(14, 2) NOT NULL CHECK ("valor" > 0),
	"forma" varchar(30),
	"conta_bancaria_id" uuid REFERENCES "contas_bancarias"("id") ON DELETE SET NULL,
	"observacao" varchar(500),
	"lancamento_empresa_id" uuid REFERENCES "lancamentos_empresa"("id") ON DELETE SET NULL,
	"lancamento_imovel_id" uuid REFERENCES "lancamentos_financeiros"("id") ON DELETE SET NULL,
	"registrado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"registrado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"estornado_em" timestamp with time zone
);

CREATE INDEX IF NOT EXISTS "ix_temporada_pagamentos_reserva" ON "temporada_pagamentos" ("reserva_id");

-- boleto/Pix do Cora também para a reserva
ALTER TABLE "fatura_boletos" ALTER COLUMN "fatura_id" DROP NOT NULL;
ALTER TABLE "fatura_boletos" ADD COLUMN IF NOT EXISTS "reserva_id" uuid REFERENCES "temporada_reservas"("id") ON DELETE CASCADE;
ALTER TABLE "fatura_boletos" ADD COLUMN IF NOT EXISTS "temporada_pagamento_id" uuid REFERENCES "temporada_pagamentos"("id") ON DELETE SET NULL;
ALTER TABLE "fatura_boletos" ADD CONSTRAINT "fatura_boletos_alvo_check" CHECK ("fatura_id" IS NOT NULL OR "reserva_id" IS NOT NULL);
CREATE INDEX IF NOT EXISTS "ix_fatura_boletos_reserva" ON "fatura_boletos" ("reserva_id");

-- pedido de reserva pelo site: aviso no sino
ALTER TABLE "alertas" ADD COLUMN IF NOT EXISTS "temporada_reserva_id" uuid REFERENCES "temporada_reservas"("id") ON DELETE CASCADE;
ALTER TYPE "public"."tipo_alerta" ADD VALUE IF NOT EXISTS 'TEMPORADA';
