-- Chaves do ASP.NET Data Protection (assinam o cookie de sessão e os tokens antifalsificação). Ficam no banco para
-- valerem em todas as instâncias e sobreviverem a deploys/reinícios do container. Só a versão .NET usa.
CREATE TABLE "data_protection_keys" (
	"id" serial PRIMARY KEY NOT NULL,
	"friendly_name" text,
	"xml" text
);
