# Inovabens — versão .NET (Blazor)

Sistema em **C# / .NET 10 + Blazor Server**. Substituiu a primeira versão em Node/Next.js (aposentada; o código
dela fica no histórico do git) e usa o mesmo banco PostgreSQL que ela usava.

| Módulo | Situação |
|---|---|
| Login (senha Argon2 + 2FA, bloqueio progressivo) | ✅ migrado |
| Imóveis: lista, cadastro, ficha, proprietários (regra dos 100%), características, fotos/documentos (Google Drive, uma pasta por imóvel), regularização | ✅ migrado |
| Financeiro (aba do imóvel): aquisição/CAPEX e itens, despesas/OPEX com checklist mensal (Não Pago exige justificativa), receitas, retorno do investimento (ROI, ROI real, TIR, MOIC, correção por IPCA/INCC/IGP-M/Selic), sincronização dos índices com o Banco Central (botão + job do dia 12) | ✅ migrado |
| Painel (resumo por situação, funil, alertas, próximos compromissos) | ✅ migrado |
| Pessoas (cadastro único com papéis: cliente, proprietário, fiador, parceiro, prestador, terceiro; LGPD) | ✅ migrado |
| Memorandos e ações do imóvel (fixar, editar, excluir) | ✅ migrado |
| Agenda: compromissos por imóvel e agenda geral, convidados, integração Google Agenda (OAuth, envio, sincronização incremental, webhook) | ✅ migrado |
| Alertas: compromisso não cumprido (job a cada 5 min) e despesa vencida (job diário 06:00, que também gera as competências do mês); reagendar/concluir pelo alerta | ✅ migrado |
| CRM: funis de venda e locação, mover estágio (perda exige motivo), novo lead, interações, imóveis de interesse, histórico | ✅ migrado |
| Relatórios: Ficha do Imóvel e Ficha Financeira em PDF (QuestPDF) e Excel (ClosedXML), registrados na auditoria | ✅ migrado |
| Administração: usuários, perfis/permissões (RBAC), cadastros (tipos de imóvel, perfis de pessoa), categorias de despesa, índices BCB, estágios do funil, auditoria | ✅ migrado |
| Minha conta: alterar senha (encerra as outras sessões) e 2FA (TOTP) | ✅ migrado |

## Estrutura

```
dotnet/
  Inovabens.slnx                 abre no Visual Studio 2026 / Rider / VS Code (C# Dev Kit)
  src/Inovabens.Core/            regras de negócio e acesso a dados (sem interface)
    Dominio/                     entidades (tabelas existentes), enums, permissões
    Dados/InovabensDb.cs         EF Core + Npgsql (sem migrations do EF)
    Dados/Migracoes/*.sql        esquema do banco (tabelas, CHECKs, triggers), aplicado pelo Migrador.cs
    Dados/DadosIniciais.cs       permissões, perfis, categorias, estágios e administrador inicial (port do seed.ts)
    Imoveis/                     ImoveisService (port de imoveis.service.ts), formulários, regras
    Financeiro/                  Aquisição, Despesas (checklist), Receitas, Índices (BCB), Retorno;
                                 CalculoRetorno.cs é o cálculo puro (port de retorno.service.ts)
    Pessoas/ Memorandos/ Agenda/ Crm/ Painel/ Admin/ Relatorios/   demais módulos (um Service por módulo)
    Servicos/                    Auth, Auditoria, escopo de acesso por imóvel, Armazenamento (Google Drive), listas de opções
  src/Inovabens.Web/             Blazor Server (telas .razor); Jobs/ = rotinas agendadas
  tests/Inovabens.Tests/         xUnit: unidade e integração (PostgreSQL no Docker)
  tests/Inovabens.E2E/           Playwright: fluxos no navegador contra a aplicação real
```

Equivalências com a antiga versão Node (para quem conhecia o código dela):

| Node | .NET |
|---|---|
| NestJS controller + service | Tela Blazor chama o *Service* direto (não há API HTTP no meio) |
| Drizzle ORM | Entity Framework Core (`IDbContextFactory`, um contexto por operação) |
| Zod | DataAnnotations + `IValidatableObject` (validados na tela **e** no serviço) |
| JWT + refresh token | Cookie de autenticação; revalida usuário/permissões no banco a cada 15 min |
| `@RequirePermission('imoveis:ver')` | `@attribute [Authorize(Policy = Permissoes.ImoveisVer)]` + `Usuario.Exigir(...)` no serviço |
| `assertAcessoImovel` (escopo do Corretor) | `Acesso.ImovelComAcesso` e o filtro `consulta.VisiveisPara(usuario)` (Servicos/Acesso.cs) |
| `audit_log` | `AuditService.Registrar` — gravado na mesma transação da operação |
| `@Cron` (`@nestjs/schedule`) | `BackgroundService` em `Inovabens.Web/Jobs` |

## Rodando

Requer o **SDK do .NET 10** e um PostgreSQL 16 (vazio ou um banco já existente do sistema).

```bash
docker compose up -d db          # banco do docker-compose do projeto
cd dotnet/src/Inovabens.Web
dotnet run                       # http://localhost:5051  — login admin@inovabens.com.br / Trocar@123456
```

**Banco:** a cada subida a aplicação aplica as migrações pendentes de `Inovabens.Core/Dados/Migracoes` e garante
os dados-base. Isso é idempotente e protegido por lock, então pode haver várias instâncias. O administrador inicial
só é criado se ainda não houver nenhum usuário (`SEED_ADMIN_EMAIL` / `SEED_ADMIN_PASSWORD`; em produção a senha
padrão é recusada). `dotnet Inovabens.Web.dll migrar` faz só essa parte e sai, para usar como passo de release.

**Importar do site atual (Imobibrasil):** `dotnet Inovabens.Web.dll importar-imobibrasil [--usuario=email]
[--site=https://www.inovabens.com.br] [--sem-fotos]` lê as fichas públicas do site e cria ou atualiza os imóveis
pelo id do anúncio (rodar de novo não duplica; só envia as fotos que faltam). As fotos vão para "04 - Fotos e
Vídeos" já públicas, sem passar pelo Magnific. Proprietários, matrícula e documentos não estão no site e ficam para
cadastrar no sistema. Sem `--usuario`, usa o primeiro Administrador ativo.

- **Banco criado pela antiga versão Node:** os 3 scripts iniciais são os mesmos que ela aplicava; são reconhecidos
  pelo diário do Drizzle e não rodam de novo. Os demais são aplicados normalmente.
- **Nova alteração de esquema:** crie `Dados/Migracoes/0006_descricao.sql` (o próximo número). Nunca edite um script
  já aplicado; o migrador avisa no log se isso acontecer.

No Visual Studio: abra `dotnet/Inovabens.slnx`, defina **Inovabens.Web** como projeto de inicialização e pressione F5.

Configuração (variáveis de ambiente, os mesmos nomes da antiga versão Node; em desenvolvimento o banco já vem de
`appsettings.Development.json`): `DATABASE_URL`, `TOKEN_ENC_KEY` (necessária para usuários com 2FA),
`GOOGLE_DRIVE_ID`, `GOOGLE_DRIVE_CREDENCIAIS` (caminho da chave JSON) ou `GOOGLE_DRIVE_CREDENCIAIS_JSON`,
`R2_ENDPOINT`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET`, `R2_URL_PUBLICA` ou `R2_CREDENCIAIS` (fotos públicas), `TRUST_PROXY`,
`DISABLE_JOBS`, `WEB_URL` (endereço público do sistema; padrão http://localhost:5051),
`GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI`, `GOOGLE_WEBHOOK_URL`,
`MAGNIFIC_API_KEY` (opcional: melhoria automática das fotos pelo Magnific, ver abaixo), `GOOGLE_MAPS_API_KEY` (opcional: Geocoding API do Google para "Localizar pelo endereço" no imóvel; sem ela usa o
OpenStreetMap), `JWT_REFRESH_SECRET`
(assina o `state` do OAuth e o token do webhook do Google — use o mesmo valor do Node). **Em produção** são
obrigatórias uma `TOKEN_ENC_KEY` aleatória e uma `JWT_REFRESH_SECRET` com 32+ caracteres, diferente da `TOKEN_ENC_KEY`:
sem elas a aplicação não sobe.
As chaves que assinam o cookie de sessão ficam na tabela `data_protection_keys`: as sessões sobrevivem a deploys e
valem em várias instâncias. Arquivos enviados só abrem no navegador se forem imagem, vídeo ou PDF; o resto (inclusive
HTML/SVG) é servido como download. Respostas levam `X-Frame-Options`, `nosniff` e uma CSP mínima (papel do helmet).
O login aceita até `LOGIN_LIMITE_POR_MINUTO` tentativas por IP (padrão 10, como na versão Node); o IP vem do
`X-Forwarded-For` do proxy (`TRUST_PROXY` = quantos proxies há na frente; padrão 1, o do Render).
Sem `GOOGLE_DRIVE_*` o sistema funciona, só o envio de fotos/documentos fica desligado; sem `GOOGLE_*`, a agenda funciona
sem a integração com o Google.

O callback e o webhook do Google ficam em `/api/v1/integracoes/google/callback` e `/api/v1/webhooks/google/calendar`
(os mesmos caminhos da antiga API Node, então a configuração no Google Cloud Console não muda) e há health check em
`/api/v1/health`. Publicação: `Dockerfile` nesta pasta e o [DEPLOY.md](../DEPLOY.md) da raiz.

**Licença do QuestPDF (PDFs):** o projeto usa a licença *Community*, gratuita para empresas com receita bruta
anual abaixo de US$ 1 milhão. Acima disso é preciso adquirir uma licença comercial (questpdf.com/license).

**Melhoria de fotos (Magnific):** com `MAGNIFIC_API_KEY` (e o Google Drive configurado), cada foto JPG/PNG/WebP enviada entra na
fila e a rotina de 1 em 1 minuto a envia ao upscaler *Precision V2* do Magnific (perfil "photo", fiel ao original):
a foto é reduzida para no máximo 2048 px e ampliada 2x (resultado de até 4096 px, regravado em JPEG). Pronta, ela
passa a ser a foto exibida (inclusive nas fichas em PDF); a original fica guardada e pode ser restaurada na tela.
Falhas (créditos, formato) ficam registradas na foto com botão para tentar de novo. A API do Magnific é cobrada
por créditos à parte da assinatura do site (docs.magnific.com/pricing).

Jobs (horário de Brasília): índices BCB no dia 12 às 06:00; alertas de compromissos a cada 5 min;
competências do mês + alertas de despesas vencidas todo dia às 06:00; sincronização Google a cada 10 min e
renovação dos canais do webhook às 04:00; melhoria de fotos pelo Magnific a cada minuto (só com `MAGNIFIC_API_KEY`). Todos são idempotentes; com várias instâncias,
use `DISABLE_JOBS=true` em todas menos uma.

## Testes

```bash
cd dotnet
dotnet test                                                   # tudo: unidade + integração + navegador (requer o Docker)
dotnet test --filter "Categoria!=Integracao&Categoria!=E2E"   # só os testes de unidade, sem Docker
dotnet test tests/Inovabens.E2E                               # só os de ponta a ponta no navegador
E2E_JANELA=1 dotnet test tests/Inovabens.E2E                  # idem, mostrando o navegador
INOVABENS_TEST_BCB=1 dotnet test                              # + consulta real ao Banco Central
```

**Ponta a ponta (`tests/Inovabens.E2E`, Playwright):** sobem a aplicação compilada como processo (como em produção),
com um banco novo, e usam o sistema por um Chromium sem janela, achando os campos pelo rótulo visível: login e
redirecionamento seguro, cadastro de imóvel, checklist financeiro (Não Pago exige justificativa), downloads de PDF/Excel,
ativação e exigência do 2FA e o escopo do Corretor (tela e download direto). Na primeira execução o Chromium é baixado.
O layout marca `data-interativo="sim"` quando o Blazor assume a página; os testes esperam por isso antes de clicar.

Os testes de integração (todos os módulos, migrações, renderização das abas, PDF/Excel) sobem um **PostgreSQL 16
descartável no Docker** (Testcontainers), aplicam as migrações e os dados-base pelo próprio .NET e criam a usuária de
teste `carla@inovabens.com.br` (Corretor + `financeiro:ver/editar`, senha `Trocar@123456`). Sem Docker eles
**falham** com uma mensagem explicando o motivo: antes eles passavam sem executar nada. Para usar um banco seu,
defina `INOVABENS_TEST_DB`. Os testes gravam dados nesse banco: **nunca aponte para o banco de produção.**
