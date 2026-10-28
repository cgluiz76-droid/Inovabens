# Inovabens — versão .NET (Blazor)

Migração do sistema Node/Next.js para **C# / .NET 10 + Blazor Server** — **todos os módulos migrados**.
As duas versões usam **o mesmo banco PostgreSQL** e podem rodar lado a lado durante a transição.

| Módulo | Situação |
|---|---|
| Login (senha Argon2 + 2FA, bloqueio progressivo) | ✅ migrado |
| Imóveis: lista, cadastro, ficha, proprietários (regra dos 100%), características, fotos/documentos (S3), regularização | ✅ migrado |
| Financeiro (aba do imóvel): aquisição/CAPEX e itens, despesas/OPEX com checklist mensal (Não Pago exige justificativa), receitas, retorno do investimento (ROI, ROI real, TIR, MOIC, correção por IPCA/INCC/IGP-M/Selic), sincronização dos índices com o Banco Central (botão + job do dia 12) | ✅ migrado |
| Painel (resumo por situação, funil, alertas, próximos compromissos) | ✅ migrado |
| Pessoas (cadastro único com papéis: cliente, proprietário, fiador, parceiro, prestador, terceiro; LGPD) | ✅ migrado |
| Memorandos e ações do imóvel (fixar, editar, excluir) | ✅ migrado |
| Agenda: compromissos por imóvel e agenda geral, convidados, integração Google Agenda (OAuth, envio, sincronização incremental, webhook) | ✅ migrado |
| Alertas: compromisso não cumprido (job a cada 5 min) e despesa vencida (job diário 06:00, que também gera as competências do mês); reagendar/concluir pelo alerta | ✅ migrado |
| CRM: funis de venda e locação, mover estágio (perda exige motivo), novo lead, interações, imóveis de interesse, histórico | ✅ migrado |
| Relatórios: Ficha do Imóvel e Ficha Financeira em PDF (QuestPDF) e Excel (ClosedXML), registrados na auditoria | ✅ migrado |
| Administração: usuários, perfis/permissões (RBAC), categorias de despesa, índices BCB, estágios do funil, auditoria | ✅ migrado |
| Minha conta: alterar senha (encerra as outras sessões) e 2FA (TOTP) | ✅ migrado |

## Estrutura

```
dotnet/
  Inovabens.slnx                 abre no Visual Studio 2026 / Rider / VS Code (C# Dev Kit)
  src/Inovabens.Core/            regras de negócio e acesso a dados (sem interface)
    Dominio/                     entidades (tabelas existentes), enums, permissões
    Dados/InovabensDb.cs         EF Core + Npgsql (sem migrations: o esquema vem de apps/api/drizzle/*.sql)
    Imoveis/                     ImoveisService (port de imoveis.service.ts), formulários, regras
    Financeiro/                  Aquisição, Despesas (checklist), Receitas, Índices (BCB), Retorno;
                                 CalculoRetorno.cs é o cálculo puro (port de retorno.service.ts)
    Pessoas/ Memorandos/ Agenda/ Crm/ Painel/ Admin/ Relatorios/   demais módulos (um Service por módulo)
    Servicos/                    Auth, Auditoria, Armazenamento S3, listas de opções
  src/Inovabens.Web/             Blazor Server (telas .razor); Jobs/ = rotinas agendadas
  tests/Inovabens.Tests/         xUnit
```

Equivalências com a versão Node:

| Node | .NET |
|---|---|
| NestJS controller + service | Tela Blazor chama o *Service* direto (não há API HTTP no meio) |
| Drizzle ORM | Entity Framework Core (`IDbContextFactory`, um contexto por operação) |
| Zod | DataAnnotations + `IValidatableObject` (validados na tela **e** no serviço) |
| JWT + refresh token | Cookie de autenticação; revalida usuário/permissões no banco a cada 15 min |
| `@RequirePermission('imoveis:ver')` | `@attribute [Authorize(Policy = Permissoes.ImoveisVer)]` + `Usuario.Exigir(...)` no serviço |
| `assertAcessoImovel` (escopo do Corretor) | `ImoveisService.ImovelComAcesso` |
| `audit_log` | `AuditService.Registrar` — gravado na mesma transação da operação |
| `@Cron` (`@nestjs/schedule`) | `BackgroundService` em `Inovabens.Web/Jobs` |

## Rodando

Requer o **SDK do .NET 10** e um PostgreSQL com as tabelas criadas (o mesmo da versão Node).

```bash
# 1) banco (do docker-compose do projeto) + tabelas e usuário admin pela versão Node, se ainda não existir
docker compose up -d db
pnpm db:migrate && pnpm db:seed

# 2) aplicação Blazor
cd dotnet/src/Inovabens.Web
dotnet run            # http://localhost:5051  — login admin@inovabens.com.br / Trocar@123456
```

No Visual Studio: abra `dotnet/Inovabens.slnx`, defina **Inovabens.Web** como projeto de inicialização e pressione F5.

Configuração (mesmos nomes de variável do `.env` da versão Node; em desenvolvimento o banco já vem de
`appsettings.Development.json`): `DATABASE_URL`, `TOKEN_ENC_KEY` (necessária para usuários com 2FA),
`S3_ENDPOINT`, `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_REGION`, `S3_FORCE_PATH_STYLE`, `TRUST_PROXY`,
`DISABLE_JOBS`, `WEB_URL` (endereço público do sistema; padrão http://localhost:5051),
`GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI`, `GOOGLE_WEBHOOK_URL`, `JWT_REFRESH_SECRET`
(assina o `state` do OAuth e o token do webhook do Google — use o mesmo valor do Node).
Sem `S3_*` o sistema funciona, só o envio de fotos/documentos fica desligado; sem `GOOGLE_*`, a agenda funciona
sem a integração com o Google.

O callback e o webhook do Google ficam nos **mesmos caminhos da API Node**
(`/api/v1/integracoes/google/callback` e `/api/v1/webhooks/google/calendar`) e há health check em `/api/v1/health`,
então a configuração no Google Cloud Console e no provedor de nuvem não muda — basta apontar o domínio para a versão .NET.

**Licença do QuestPDF (PDFs):** o projeto usa a licença *Community*, gratuita para empresas com receita bruta
anual abaixo de US$ 1 milhão. Acima disso é preciso adquirir uma licença comercial (questpdf.com/license).

Jobs (horário de Brasília, os mesmos do Node): índices BCB no dia 12 às 06:00; alertas de compromissos a cada 5 min;
competências do mês + alertas de despesas vencidas todo dia às 06:00; sincronização Google a cada 10 min e
renovação dos canais do webhook às 04:00. Todos são idempotentes; se as duas versões rodarem juntas,
use `DISABLE_JOBS=true` em uma delas.

## Testes

```bash
cd dotnet
dotnet test                       # regras de negócio + compatibilidade com o Node + mapeamento EF x SQL
# testes de integração (todos os módulos, renderização das abas, PDF/Excel) contra um banco de TESTE:
INOVABENS_TEST_DB=postgres://inovabens:inovabens@localhost:5433/inovabens dotnet test
# + consulta real ao Banco Central:
INOVABENS_TEST_BCB=1 INOVABENS_TEST_DB=... dotnet test
```

Os testes de integração esperam um banco com as migrations aplicadas, ao menos uma categoria em
`despesa_categorias`, os estágios padrão em `funil_estagios` (como no `pnpm db:seed`) e os usuários
`admin@inovabens.com.br` (Administrador) e `carla@inovabens.com.br` (Corretor, escopo próprio, com as permissões
padrão do Corretor mais `financeiro:ver` e `financeiro:editar`) com a senha `Trocar@123456`. Eles gravam dados:
**não aponte para o banco de produção.**
