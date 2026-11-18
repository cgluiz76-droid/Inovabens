# Inovabens — guia rápido para o Claude

Sistema de gestão imobiliária da Inova Bens: imóveis, pessoas, CRM, agenda, financeiro, site público e portais.
Respostas e textos sempre em **português**. Produção: https://inovabens.fly.dev (Fly.io, região gru).

## Stack e estrutura
- **.NET 10 + Blazor Server** (SSR + interativo), **EF Core/Npgsql**, PostgreSQL (produção 18, local 16 no docker-compose).
- `dotnet/src/Inovabens.Core/` — regras de negócio e dados, sem UI. Uma pasta por módulo: `Imoveis`, `Pessoas`, `Crm`,
  `Agenda`, `Financeiro`, `Relatorios`, `Admin`, `Painel`, `Busca`, `Memorandos`, `Redes`, `Notion`, `Servicos` (integrações).
  - `Dominio/` entidades, enums, `Permissoes.cs` (catálogo RBAC), `PadroesIniciais.cs` (perfis padrão).
  - `Dados/InovabensDb.cs` (EF, **sem migrations do EF**), `Dados/Migracoes/NNNN_nome.sql` (esquema; aplicado por `Migrador.cs`
    ao subir), `Dados/DadosIniciais.cs` (seed idempotente: permissões e perfis — o Administrador recebe `Permissoes.Todas`).
  - `Comum/` `UsuarioAtual`, `Texto` (rótulos, moeda, `Limpo`), `Formatos` (CPF/CNPJ, telefone), `DatasBR`, exceções.
- `dotnet/src/Inovabens.Web/` — `Program.cs` (DI, políticas por permissão, endpoints de download/API, comandos CLI),
  `Components/Pages/*.razor` (telas), `Components/Shared/` (Modal, Paginacao, MensagemErro, `ComponenteBase`),
  `Components/Site/` (site público), `Jobs/`, `wwwroot/app.css` (tokens de cor/espaço em `:root`), `site-previa.css`, `interface.js`.
- `dotnet/tests/Inovabens.Tests/` (xUnit, integração contra Postgres real via Docker) e `dotnet/tests/Inovabens.E2E/` (Playwright).

## Padrões do código
- Serviço recebe `UsuarioAtual u` e chama `u.Exigir(P.Xxx)` (defesa além da tela); escopo do Corretor via `.VisiveisPara(u)`.
- Tela: `@inherits ComponenteBase`, `[Authorize(Policy = Permissoes.Xxx)]`, `Executar(async () => …)` trata erros; botão só com `Usuario.Pode(...)`.
- Permissão nova: constante em `Permissoes.cs` + incluir em `Todas` (o seed cria e dá ao Administrador; perfil Admin é protegido).
  Rótulo na tela de perfis = parte após `:` com `_` virando espaço.
- Mudança de banco: novo `Dados/Migracoes/NNNN_*.sql` (próximo número), idempotente quando possível.
- Excel: `RelatoriosService.Aba(...)` + `Salvar(wb)` → `Arquivo`; endpoint `app.MapGet("….xlsx", … Baixar(c, u => …))
  .RequireAuthorization(permissão)`; botão `<a … download>` com filtros da tela na query; registrar `audit.Registrar(... "EXPORTAR")`.
- Fotos/arquivos: Google Drive (privado) e Cloudflare R2 (fotos públicas do site). Mapa: só Google Maps.
- Comentários e textos de UI em português, curtos e explicando o porquê.

## Comandos (a partir de `dotnet/`)
- Build: `dotnet build src/Inovabens.Web --nologo -v q`
- Testes **da função alterada**: `dotnet test tests/Inovabens.Tests --filter "FullyQualifiedName~NomeDaClasse"` (precisa do Docker).
- Suíte geral (~400 testes) / E2E / medição Lighthouse: **só com OK do usuário** — recomendar e justificar antes
  (mudança grande, efeito global: auth/permissões, CSS global, migração, Program.cs; ou várias pequenas acumuladas).
- Verificação local da tela: `dotnet publish src/Inovabens.Web -c Release -o <scratchpad>/bld` e rodar
  `ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5063 dotnet Inovabens.Web.dll` (só quando a mudança é visual;
  preferir medir no DOM a tirar print). Encerrar só os servidores que eu mesmo subi.
- Deploy (só com OK explícito): `~/.fly/bin/flyctl deploy --remote-only` em `dotnet/`. Banco: app `inovabens-db`.
- Backup do banco: `Documents/Inova Bens/Backups Banco/` (ver `LEIA-ME.txt` lá); restaurar só com confirmação.

## Regras
- Commit e push só quando o usuário pedir; deploy em produção só com autorização. Branch atual de trabalho: `armazenamento-google-drive`.
- **Nunca** exibir nem commitar segredos: `Fly.md`, `ChaveMagnific.txt`, `NotionToken.txt`, `client_secret_*.json`,
  `google-drive*.json`, `appsettings.Development.json`, cookies de sessão. Usuário digita senhas/segredos na tela.
- Não commitar `.claude/launch.json` nem `dotnet/src/Inovabens.Core/MelhoriaFotosService.cs` (trabalho do usuário em andamento).
- Imobibrasil: só leitura. Notion: não alterar. Não perder dados (backup antes de ação destrutiva).
- Economia de tokens: pedidos em lote; ler só os arquivos necessários (Grep antes de Read); sem print quando medir basta.
