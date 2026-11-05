-- Configurações das integrações editadas em Administração → Configurações. Têm prioridade sobre as variáveis de
-- ambiente; segredos (chaves, tokens) ficam cifrados com a TOKEN_ENC_KEY (AES-256-GCM, como o segredo do 2FA).
CREATE TABLE "configuracoes" (
	"chave" varchar(80) PRIMARY KEY NOT NULL,
	"valor" text NOT NULL,
	"cifrado" boolean DEFAULT false NOT NULL,
	"atualizado_em" timestamp with time zone DEFAULT now() NOT NULL,
	"atualizado_por" uuid REFERENCES "usuarios"("id") ON DELETE SET NULL
);
