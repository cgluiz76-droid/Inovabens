-- Controle de chaves dos imóveis (ideia do módulo do Imobibrasil, simplificada): cada chave física tem um código de
-- etiqueta (CH0001...) e cada saída vira um movimento com quem levou, por quê e até quando devolve. A chave está
-- "fora" enquanto houver movimento sem devolução; passado o prazo, aparece como atrasada.
CREATE TABLE "chaves" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"imovel_id" uuid NOT NULL REFERENCES "imoveis"("id") ON DELETE CASCADE,
	"codigo" varchar(20) NOT NULL,
	"descricao" varchar(120) NOT NULL,
	"observacao" varchar(500),
	"criado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	-- exclusão lógica: o histórico de quem usou a chave continua consultável
	"excluido_em" timestamp with time zone
);

-- o código é a etiqueta física: nunca se repete, nem com chave excluída
CREATE UNIQUE INDEX IF NOT EXISTS "ux_chaves_codigo" ON "chaves" (upper("codigo"));
CREATE INDEX IF NOT EXISTS "ix_chaves_imovel" ON "chaves" ("imovel_id");

CREATE TABLE "chave_movimentos" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"chave_id" uuid NOT NULL REFERENCES "chaves"("id") ON DELETE CASCADE,
	-- com quem a chave está: um usuário da equipe, uma pessoa do cadastro ou só um nome anotado
	-- (pessoa_nome/telefone guardam o que valia na saída, mesmo que o cadastro mude depois)
	"usuario_id" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"pessoa_id" uuid REFERENCES "pessoas"("id") ON DELETE SET NULL,
	"pessoa_nome" varchar(200),
	"pessoa_telefone" varchar(30),
	"motivo" varchar(40) NOT NULL,
	"motivo_detalhe" varchar(200),
	"retirada_em" timestamp with time zone NOT NULL,
	"previsao_devolucao" timestamp with time zone NOT NULL,
	"devolvida_em" timestamp with time zone,
	"observacao_devolucao" varchar(300),
	"registrado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	"devolucao_registrada_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL,
	CONSTRAINT "ck_chave_movimento_com_quem" CHECK ("usuario_id" IS NOT NULL OR "pessoa_nome" IS NOT NULL)
);

-- uma chave só pode estar fora uma vez (a próxima saída exige registrar a devolução)
CREATE UNIQUE INDEX IF NOT EXISTS "ux_chave_movimentos_aberto" ON "chave_movimentos" ("chave_id") WHERE "devolvida_em" IS NULL;
CREATE INDEX IF NOT EXISTS "ix_chave_movimentos_chave" ON "chave_movimentos" ("chave_id", "retirada_em");
