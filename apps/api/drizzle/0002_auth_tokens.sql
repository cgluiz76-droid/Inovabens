CREATE TABLE "refresh_tokens" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"usuario_id" uuid NOT NULL,
	"familia_id" uuid NOT NULL,
	"token_hash" varchar(64) NOT NULL,
	"expira_em" timestamp with time zone NOT NULL,
	"revogado_em" timestamp with time zone,
	"ip" varchar(64),
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "refresh_tokens_token_hash_unique" UNIQUE("token_hash")
);
--> statement-breakpoint
CREATE TABLE "senha_resets" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"usuario_id" uuid NOT NULL,
	"token_hash" varchar(64) NOT NULL,
	"expira_em" timestamp with time zone NOT NULL,
	"usado_em" timestamp with time zone,
	"criado_em" timestamp with time zone DEFAULT now() NOT NULL,
	CONSTRAINT "senha_resets_token_hash_unique" UNIQUE("token_hash")
);
--> statement-breakpoint
DROP INDEX "ux_alerta_lanc_aberto";--> statement-breakpoint
ALTER TABLE "refresh_tokens" ADD CONSTRAINT "refresh_tokens_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
ALTER TABLE "senha_resets" ADD CONSTRAINT "senha_resets_usuario_id_usuarios_id_fk" FOREIGN KEY ("usuario_id") REFERENCES "public"."usuarios"("id") ON DELETE cascade ON UPDATE no action;--> statement-breakpoint
CREATE INDEX "ix_refresh_usuario" ON "refresh_tokens" USING btree ("usuario_id");--> statement-breakpoint
CREATE INDEX "ix_refresh_familia" ON "refresh_tokens" USING btree ("familia_id");--> statement-breakpoint
CREATE UNIQUE INDEX "ux_alerta_lanc_aberto" ON "alertas" USING btree ("lancamento_id","tipo","usuario_id") WHERE "alertas"."resolvido_em" IS NULL AND "alertas"."lancamento_id" IS NOT NULL;