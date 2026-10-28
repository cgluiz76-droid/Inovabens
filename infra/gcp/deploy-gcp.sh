#!/usr/bin/env bash
# Publica o Inovabens (.NET / Blazor Server) no Google Cloud: Cloud Run (um serviço), Cloud SQL (PostgreSQL 16),
# Cloud Storage (arquivos) e Secret Manager.
#
# Uso (no Cloud Shell ou em um terminal com gcloud logado):
#   export PROJECT_ID=meu-projeto            # projeto com faturamento ativo
#   export SEED_ADMIN_EMAIL=voce@empresa.com # administrador inicial
#   bash infra/gcp/deploy-gcp.sh
# Pode ser executado de novo para atualizar (não recria banco, bucket nem segredos existentes).
set -euo pipefail

: "${PROJECT_ID:?defina PROJECT_ID}"
: "${SEED_ADMIN_EMAIL:?defina SEED_ADMIN_EMAIL}"
REGION="${REGION:-southamerica-east1}"
SQL_INSTANCE="${SQL_INSTANCE:-inovabens-db}"
SQL_TIER="${SQL_TIER:-db-f1-micro}"
DB_NAME="${DB_NAME:-inovabens}"
DB_USER="${DB_USER:-inovabens}"
BUCKET="${BUCKET:-${PROJECT_ID}-inovabens-arquivos}"
AR_REPO="${AR_REPO:-inovabens}"
SA_NAME="inovabens-run"
TAG="${TAG:-$(date +%Y%m%d-%H%M%S)}"
cd "$(dirname "$0")/../.."

g() { gcloud --project "$PROJECT_ID" "$@"; }
existe_secret() { g secrets describe "$1" >/dev/null 2>&1; }
novo_secret() { # nome valor — cria só se não existir
  if existe_secret "$1"; then echo "  segredo $1 já existe (mantido)"; else printf '%s' "$2" | g secrets create "$1" --data-file=- --replication-policy=automatic >/dev/null; echo "  segredo $1 criado"; fi
}

echo "▶ 1/7 Ativando APIs"
g services enable run.googleapis.com sqladmin.googleapis.com artifactregistry.googleapis.com cloudbuild.googleapis.com secretmanager.googleapis.com storage.googleapis.com iam.googleapis.com

PROJECT_NUMBER="$(g projects describe "$PROJECT_ID" --format='value(projectNumber)')"
WEB_URL="https://inovabens-web-${PROJECT_NUMBER}.${REGION}.run.app"
REPO="${REGION}-docker.pkg.dev/${PROJECT_ID}/${AR_REPO}"
SA="${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

echo "▶ 2/7 Conta de serviço e repositório de imagens"
g iam service-accounts describe "$SA" >/dev/null 2>&1 || g iam service-accounts create "$SA_NAME" --display-name="Inovabens (Cloud Run)"
for role in roles/cloudsql.client roles/secretmanager.secretAccessor; do
  g projects add-iam-policy-binding "$PROJECT_ID" --member="serviceAccount:$SA" --role="$role" --condition=None >/dev/null
done
g artifacts repositories describe "$AR_REPO" --location="$REGION" >/dev/null 2>&1 || \
  g artifacts repositories create "$AR_REPO" --repository-format=docker --location="$REGION"

echo "▶ 3/7 Cloud SQL (PostgreSQL 16) — a criação leva ~5-10 min na primeira vez"
if ! g sql instances describe "$SQL_INSTANCE" >/dev/null 2>&1; then
  g sql instances create "$SQL_INSTANCE" --database-version=POSTGRES_16 --edition=ENTERPRISE --tier="$SQL_TIER" --region="$REGION" \
    --storage-size=10GB --storage-auto-increase --backup-start-time=05:00 --deletion-protection
fi
g sql databases describe "$DB_NAME" --instance="$SQL_INSTANCE" >/dev/null 2>&1 || g sql databases create "$DB_NAME" --instance="$SQL_INSTANCE"
SQL_CONN="${PROJECT_ID}:${REGION}:${SQL_INSTANCE}"

echo "▶ 4/7 Segredos"
if ! existe_secret inovabens-database-url; then
  DB_PASS="$(openssl rand -hex 24)"
  if g sql users list --instance="$SQL_INSTANCE" --format='value(name)' | grep -qx "$DB_USER"; then
    g sql users set-password "$DB_USER" --instance="$SQL_INSTANCE" --password="$DB_PASS"
  else
    g sql users create "$DB_USER" --instance="$SQL_INSTANCE" --password="$DB_PASS"
  fi
  novo_secret inovabens-database-url "postgres://${DB_USER}:${DB_PASS}@/${DB_NAME}?host=/cloudsql/${SQL_CONN}"
else echo "  segredo inovabens-database-url já existe (mantido)"; fi
novo_secret inovabens-jwt-refresh "$(openssl rand -hex 32)"
novo_secret inovabens-token-enc-key "$(openssl rand -hex 32)"
if ! existe_secret inovabens-admin-password; then
  ADMIN_PASS="$(openssl rand -base64 18 | tr -d '/+=' | cut -c1-16)Aa1"
  novo_secret inovabens-admin-password "$ADMIN_PASS"
  echo "  >>> SENHA INICIAL DO ADMINISTRADOR (anote e troque no primeiro acesso): $ADMIN_PASS"
fi

echo "▶ 5/7 Cloud Storage (bucket privado) + chave de acesso S3-compatível"
g storage buckets describe "gs://${BUCKET}" >/dev/null 2>&1 || \
  g storage buckets create "gs://${BUCKET}" --location="$REGION" --uniform-bucket-level-access --public-access-prevention
g storage buckets add-iam-policy-binding "gs://${BUCKET}" --member="serviceAccount:$SA" --role=roles/storage.objectAdmin >/dev/null
# CORS para exibir imagens por URL assinada
printf '[{"origin":["%s"],"method":["GET"],"maxAgeSeconds":3600}]' "$WEB_URL" > /tmp/inovabens-cors.json
g storage buckets update "gs://${BUCKET}" --cors-file=/tmp/inovabens-cors.json >/dev/null
if ! existe_secret inovabens-s3-access-key; then
  HMAC="$(g storage hmac create "$SA" --format='value(metadata.accessId,secret)')"
  novo_secret inovabens-s3-access-key "$(echo "$HMAC" | cut -f1)"
  novo_secret inovabens-s3-secret-key "$(echo "$HMAC" | cut -f2)"
else echo "  chave HMAC já existe (mantida)"; fi

echo "▶ 6/7 Construindo a imagem (Cloud Build) — tag $TAG"
g builds submit . --config infra/gcp/cloudbuild.yaml --substitutions="_REPO=${REPO},_TAG=${TAG}"

echo "▶ 7/7 Publicando o sistema (Cloud Run) — migrações e dados iniciais rodam na subida"
# 1 instância sempre ligada e com CPU alocada: os jobs agendados (alertas, índices, Google) rodam no próprio serviço.
# Blazor Server mantém um WebSocket por tela aberta: timeout de 1 h (o padrão de 300 s derrubaria a conexão) e
# afinidade de sessão. O nome inovabens-web mantém o endereço da antiga versão Node (Google OAuth não muda).
g run deploy inovabens-web --image="${REPO}/app:${TAG}" --region="$REGION" --service-account="$SA" --port=8080   --allow-unauthenticated --min-instances=1 --max-instances=1 --no-cpu-throttling --memory=1Gi --cpu=1   --timeout=3600 --session-affinity   --add-cloudsql-instances="$SQL_CONN"   --set-env-vars="WEB_URL=${WEB_URL},SEED_ADMIN_EMAIL=${SEED_ADMIN_EMAIL},S3_ENDPOINT=https://storage.googleapis.com,S3_REGION=auto,S3_BUCKET=${BUCKET},S3_FORCE_PATH_STYLE=true,GOOGLE_REDIRECT_URI=${WEB_URL}/api/v1/integracoes/google/callback,GOOGLE_WEBHOOK_URL=${WEB_URL}/api/v1/webhooks/google/calendar"   --set-secrets="DATABASE_URL=inovabens-database-url:latest,JWT_REFRESH_SECRET=inovabens-jwt-refresh:latest,TOKEN_ENC_KEY=inovabens-token-enc-key:latest,SEED_ADMIN_PASSWORD=inovabens-admin-password:latest,S3_ACCESS_KEY=inovabens-s3-access-key:latest,S3_SECRET_KEY=inovabens-s3-secret-key:latest"

REAL_WEB="$(g run services describe inovabens-web --region="$REGION" --format='value(status.url)')"
echo
echo "✅ Publicado."
echo "   Site:  $REAL_WEB"
echo "   Login: $SEED_ADMIN_EMAIL  (senha inicial impressa no passo 4, ou: gcloud secrets versions access latest --secret=inovabens-admin-password)"
[ "$REAL_WEB" = "$WEB_URL" ] || echo "⚠ A URL real difere da prevista ($WEB_URL). Rode: gcloud run services update inovabens-web --region $REGION --update-env-vars WEB_URL=$REAL_WEB"
if g run services describe inovabens-api --region="$REGION" >/dev/null 2>&1; then
  echo "ℹ O serviço inovabens-api (antiga API Node) não é mais usado. Para removê-lo:"
  echo "  gcloud run services delete inovabens-api --region $REGION"
fi
echo
echo "Google Calendar (opcional): crie as credenciais OAuth e rode"
echo "  gcloud run services update inovabens-web --region $REGION --update-env-vars GOOGLE_CLIENT_ID=...,GOOGLE_CLIENT_SECRET=..."
echo "  URI de redirecionamento autorizado: ${REAL_WEB}/api/v1/integracoes/google/callback"
