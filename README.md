# Inovabens — Gestão Imobiliária e Financeira

Projeto Controle de Projetos e Processos da Inovabens.

Sistema web para a Inovabens (investimento imobiliário): ficha dos imóveis, custo de aquisição (CAPEX) e despesas recorrentes (OPEX) com checklist mensal, retorno do investimento corrigido por índice (IPCA/INCC/IGP-M/Selic), memorandos, agenda com Google Calendar, alertas, CRM em kanban e controle de acesso por perfil.

## Arquitetura

| Camada | Tecnologia |
|---|---|
| Aplicação | C# / .NET 10, Blazor Server (telas e regras no servidor, sem API HTTP intermediária) |
| Banco | PostgreSQL 16 + EF Core (Npgsql); esquema em scripts SQL versionados em `dotnet/src/Inovabens.Core/Dados/Migracoes` |
| Arquivos | S3 compatível (AWS S3, Cloudflare R2, MinIO, Cloud Storage) com URLs assinadas |
| Relatórios | PDF (QuestPDF) e Excel (ClosedXML) no servidor |
| Testes | xUnit + Testcontainers (PostgreSQL real) e Playwright (navegador) |

Detalhes de código, configuração e testes: **[dotnet/README.md](dotnet/README.md)**.

### Regras de negócio garantidas no banco (não só na tela)
- `Total de aquisição = valor pago + impostos + taxas + despesas diversas`: coluna gerada; a tela nunca a envia. Itens detalhados recalculam os componentes por trigger.
- Despesa **Não Paga exige justificativa** (≥ 10 caracteres): validada no serviço e por `CHECK` no banco.
- Um alerta aberto por compromisso vencido; um por despesa vencida e usuário (índices únicos parciais).
- Auditoria (`audit_log`) append-only: triggers bloqueiam UPDATE/DELETE.
- Soma dos proprietários vigentes = 100%.

### Retorno do investimento
`custo total = aquisição + despesas pagas` · `receita líquida = bruto − custos − tributos` · ROI, **ROI real** (custo corrigido pelo índice entre a data de cada saída e a data final), **MOIC**, **TIR anual** (XIRR) e prazo. Despesas **Não Pagas** ficam como *passivo pendente*, fora do custo.

## Rodando localmente

### Opção A — tudo em contêineres (mais simples)
Requer Docker Desktop. Na pasta do projeto:
```bash
docker compose up --build
```
Abra http://localhost:5051 e entre com `admin@inovabens.com.br` / `Trocar@123456` (troque em *Minha conta*). Nesse modo o envio de fotos/documentos fica **desligado** (não é necessário para testar o restante). Para ligá-lo, crie um arquivo `.env` ao lado do `docker-compose.yml` com
```
S3_ENDPOINT=http://minio:9000
S3_ACCESS_KEY=minioadmin
S3_SECRET_KEY=minioadmin
```
e rode `docker compose --profile arquivos up --build` (sobe também um MinIO local, console em http://localhost:9001, `minioadmin`/`minioadmin`).

### Opção B — desenvolvimento (Visual Studio / `dotnet run`)
Requer o SDK do .NET 10 e um PostgreSQL 16 (o compose fornece um: `docker compose up -d db`).
```bash
cd dotnet/src/Inovabens.Web
dotnet run                       # http://localhost:5051
```
No Visual Studio: abra `dotnet/Inovabens.slnx`, defina **Inovabens.Web** como projeto de inicialização e pressione F5. No Windows, `EXECUTE.BAT` faz o mesmo e abre o Chrome. A aplicação cria as tabelas e o administrador inicial na primeira subida.

**O que conferir:** (1) login; (2) *Imóveis → Novo imóvel*; (3) aba *Financeiro*: lance a aquisição e um item de imposto e veja o total somar; cadastre uma despesa e marque "Não pago" (a justificativa é obrigatória); (4) lance uma receita de venda e veja ROI/TIR; (5) *Agenda*: crie um compromisso no passado e aguarde até 5 minutos para o alerta (sino no topo); (6) *CRM*: arraste um lead entre as colunas; (7) baixe a Ficha do Imóvel e a Ficha Financeira (PDF/Excel).

## Testes
```bash
cd dotnet
dotnet test                                                   # unidade + integração + navegador (requer o Docker)
dotnet test --filter "Categoria!=Integracao&Categoria!=E2E"   # só os de unidade, sem Docker
```

## Publicação em nuvem
Veja **[DEPLOY.md](DEPLOY.md)** (Render com `render.yaml`, Google Cloud Run ou qualquer host de contêineres).

> A primeira versão do sistema foi escrita em Node.js (NestJS + Next.js) e foi aposentada após a migração para .NET.
> O código dela continua no histórico do git; bancos criados por ela são reconhecidos e atualizados pela versão .NET.
