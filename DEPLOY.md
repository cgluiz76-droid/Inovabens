# Publicação em nuvem

O sistema são **dois contêineres** (`apps/api/Dockerfile`, `apps/web/Dockerfile`, build a partir da raiz) mais um **PostgreSQL 16** e um **bucket S3**. O `render.yaml` sobe tudo no Render; os mesmos passos valem para Railway, Fly.io, Google Cloud Run etc.

## 1. Pré-requisitos
1. **Banco**: PostgreSQL 16 gerenciado (o blueprint cria um no Render).
2. **Arquivos**: bucket S3 compatível (Cloudflare R2 é uma opção barata). Crie o bucket e uma chave de acesso. Sem isso o sistema funciona, mas o envio de fotos/documentos responde 503.
3. **Segredos**: gere `TOKEN_ENC_KEY` com `openssl rand -hex 32`. `JWT_ACCESS_SECRET` e `JWT_REFRESH_SECRET` precisam ser diferentes entre si, com 32+ caracteres (o Render gera sozinho).

## 2. Render (blueprint)
1. Suba o repositório no GitHub.
2. Render → **New → Blueprint** → selecione o repositório (usa `render.yaml`).
3. Preencha as variáveis `sync:false` no serviço `inovabens-api`:
   - `WEB_URL` e `CORS_ORIGIN` = URL pública do `inovabens-web` (ex.: `https://inovabens-web.onrender.com`)
   - `TOKEN_ENC_KEY`, `SEED_ADMIN_EMAIL`, `SEED_ADMIN_PASSWORD` (senha forte; em produção o seed **recusa** a senha padrão)
   - `S3_ENDPOINT`, `S3_BUCKET`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`
4. Ao subir, a API aplica as migrations, cria perfis/categorias/estágios padrão e o administrador inicial (só se ainda não houver usuários), e inicia. Verifique `https://SUA-API/api/v1/health`.
5. Mantenha **1 réplica** da API: os jobs agendados rodam dentro dela (alertas, lançamentos mensais, índices do Banco Central, sincronização Google). Para escalar, defina `DISABLE_JOBS=true` em todas as réplicas menos uma.

O navegador só conversa com o **web**; ele repassa `/api/v1/*` para a API pela rede interna (`API_INTERNAL_URL`, lida em tempo de execução). Assim o cookie de sessão é *first-party* e não há problema de CORS/cookie de terceiros. A API não precisa ficar exposta publicamente (o health check e o Swagger ficam acessíveis só se você a expuser).

## 3. Google Calendar (opcional)
1. Google Cloud Console → crie um projeto → **APIs e serviços → Biblioteca** → ative *Google Calendar API*.
2. **Tela de consentimento OAuth** (escopo `https://www.googleapis.com/auth/calendar`) e **Credenciais → ID do cliente OAuth (aplicativo da Web)**.
3. URI de redirecionamento autorizado: `https://SEU-WEB/api/v1/integracoes/google/callback`.
4. Na API defina `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI` (a mesma URI) e, para sincronização quase em tempo real, `GOOGLE_WEBHOOK_URL=https://SEU-WEB/api/v1/webhooks/google/calendar` (HTTPS público). Sem webhook a sincronização ocorre por *polling* a cada 10 minutos.
5. Em **Agenda → Conectar Google Calendar** cada usuário autoriza a própria conta.

Comportamento: compromissos criados no sistema vão para o Google; alterações em qualquer lado são refletidas (vence a atualização mais recente). Eventos criados direto no Google só entram no sistema se o título/descrição citarem o código do imóvel, ex.: `Visita #IMV-000012`.

## 4. Checklist de produção
- [ ] Trocar a senha do administrador inicial e ativar 2FA (*Minha conta*).
- [ ] `COOKIE_SECURE=true` e HTTPS no domínio do web (o blueprint já define).
- [ ] Backup automático do PostgreSQL ativado no provedor (inclui `audit_log`).
- [ ] Bucket S3 **privado** (o sistema entrega URLs assinadas temporárias).
- [ ] Criar os usuários reais e atribuir perfis (Administrador, Corretor, Financeiro) em *Administração*.
- [ ] Sincronizar os índices (*Administração → Configurações → Índices econômicos*) ou aguardar o job mensal.

## 5. Atualizações
Cada deploy executa `migrate → seed → start` (idempotente). Novas migrations: `pnpm db:generate` em desenvolvimento e commit da pasta `apps/api/drizzle`.

## 6. Google Cloud (Cloud Run + Cloud SQL + Cloud Storage)

Alternativa ao Render, tudo no Google. **Não** usa o Google Drive (o Drive não executa aplicações); os arquivos do sistema ficam em um bucket do **Cloud Storage** privado e o calendário continua sendo o **Google Calendar**.

| Peça | Serviço |
|---|---|
| API e site | Cloud Run (`inovabens-api`, `inovabens-web`) |
| Banco | Cloud SQL for PostgreSQL 16 (`db-f1-micro`, backup diário, proteção contra exclusão) |
| Fotos/documentos | Cloud Storage via protocolo S3 (chaves HMAC) |
| Segredos | Secret Manager |

**Pré-requisito:** um projeto no Google Cloud com faturamento ativo.

```bash
# no Cloud Shell (console.cloud.google.com → ícone >_) com o código do projeto descompactado
export PROJECT_ID=seu-projeto
export SEED_ADMIN_EMAIL=voce@suaempresa.com
bash infra/gcp/deploy-gcp.sh
```
O script é idempotente: rode de novo para publicar uma nova versão (não recria banco, bucket nem segredos). Ele imprime a URL do site e a senha inicial do administrador; troque-a no primeiro acesso.

Pontos de atenção:
- A API roda com **1 instância sempre ligada e CPU sempre alocada** (`--min-instances=1 --no-cpu-throttling`), pois os jobs agendados rodam dentro dela. Isso é o principal custo fixo, além do Cloud SQL; consulte a calculadora de preços do Google.
- O site repassa `/api/v1/*` para a API (`API_INTERNAL_URL`), então o cookie de sessão é do mesmo domínio. `TRUST_PROXY=2` faz a API enxergar o IP real do usuário atrás dos dois proxies.
- O domínio padrão é `*.run.app`; para domínio próprio use *Cloud Run → Gerenciar domínios personalizados* no serviço `inovabens-web` e atualize `WEB_URL`/`CORS_ORIGIN`/`GOOGLE_REDIRECT_URI`.
- Para o Google Calendar, siga a seção 3 usando a URL do site do Cloud Run.
- O script **não foi executado contra uma conta real do Google Cloud** por quem o escreveu; o primeiro deploy pode exigir pequenos ajustes (por exemplo, permissões da conta que o executa: Proprietário ou Editor do projeto).
