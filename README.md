# Inovabens — Gestão Imobiliária e Financeira

Sistema web para a Inovabens (investimento imobiliário): ficha dos imóveis, custo de aquisição (CAPEX) e despesas recorrentes (OPEX) com checklist mensal, retorno do investimento corrigido por índice (IPCA/INCC/IGP-M/Selic), memorandos, agenda com Google Calendar, alertas, CRM em kanban e controle de acesso por perfil.

## Arquitetura

| Camada | Tecnologia |
|---|---|
| Web | Next.js 15 (React 19), Tailwind, TanStack Query, FullCalendar, dnd-kit |
| API | NestJS 11 (REST em `/api/v1`, Swagger em `/api/docs`), Zod, JWT + refresh rotativo, RBAC por permissão |
| Banco | PostgreSQL 16 + Drizzle ORM (migrations SQL versionadas em `apps/api/drizzle`) |
| Arquivos | S3 compatível (AWS S3, Cloudflare R2, MinIO) com URLs assinadas |
| Relatórios | PDF (pdfkit) e Excel (exceljs) no servidor |

```
apps/api        API NestJS (módulos: auth, rbac, users, pessoas, imoveis, financeiro, memorandos, agenda, alertas, crm, relatorios, indices, dashboard, auditoria)
apps/web        Interface Next.js (o navegador fala só com a própria origem; /api/v1 é repassado à API)
packages/shared Enums, permissões e perfis padrão compartilhados
docs/API.md     Contrato da API
```

### Regras de negócio garantidas no banco (não só na tela)
- `Total de aquisição = valor pago + impostos + taxas + despesas diversas`: coluna gerada; o cliente nunca a envia. Itens detalhados recalculam os componentes por trigger.
- Despesa **Não Paga exige justificativa** (≥ 10 caracteres): validada na API (422) e por `CHECK` no banco.
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
Abra http://localhost:3000 e entre com `admin@inovabens.com.br` / `Trocar@123456` (troque em *Minha conta*). Nesse modo o envio de fotos/documentos fica **desligado** (não é necessário para testar o restante). Para ligá-lo, crie um arquivo `.env` ao lado do `docker-compose.yml` com
```
S3_ENDPOINT=http://minio:9000
S3_ACCESS_KEY=minioadmin
S3_SECRET_KEY=minioadmin
```
e rode `docker compose --profile arquivos up --build` (sobe também um MinIO local, console em http://localhost:9001, `minioadmin`/`minioadmin`).

### Opção B — modo desenvolvimento (recarrega ao salvar)
Requer Node 22, pnpm 10 (`corepack enable`) e um PostgreSQL 16 (o compose fornece um: `docker compose up -d db`).
```bash
cp .env.example .env            # já aponta para o banco do compose
pnpm install
pnpm db:migrate && pnpm db:seed # tabelas, perfis, categorias, estágios e o administrador inicial
pnpm dev:api                    # terminal 1 → http://localhost:3001 (Swagger em /api/docs)
pnpm dev:web                    # terminal 2 → http://localhost:3000
```
Sem Docker, use qualquer PostgreSQL 16 e ajuste `DATABASE_URL` no `.env`. Sem S3 o sistema funciona, só o envio de fotos/documentos fica desligado (responde 503). Para usar arquivos localmente: `docker compose --profile arquivos up -d minio` e mantenha as variáveis `S3_*` do `.env.example`.

**O que conferir:** (1) login; (2) *Imóveis → Novo imóvel*; (3) aba *Financeiro*: lance a aquisição e um item de imposto e veja o total somar; cadastre uma despesa e marque "Não pago" (a justificativa é obrigatória); (4) lance uma receita de venda e veja ROI/TIR; (5) *Agenda*: crie um compromisso no passado e aguarde até 5 minutos para o alerta (sino no topo); (6) *CRM*: arraste um lead entre as colunas; (7) baixe a Ficha do Imóvel e a Ficha Financeira (PDF/Excel).

## Testes
```bash
pnpm test        # 49 testes: regras do banco, auth/RBAC, imóveis, financeiro/retorno, agenda/alertas, CRM, relatórios, Google (mapeamentos)
pnpm typecheck && pnpm build
```
Os testes usam PGlite (PostgreSQL em WebAssembly), sem precisar de banco instalado.

## Publicação em nuvem
Veja **[DEPLOY.md](DEPLOY.md)** (Render com `render.yaml`, ou qualquer host de contêineres).
