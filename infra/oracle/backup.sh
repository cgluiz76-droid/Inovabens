#!/usr/bin/env bash
# Cópia do banco (pg_dump comprimido) em infra/oracle/backups, guardando os últimos 14 dias.
# O instalar.sh agenda este script às 3h. Restaurar:
#   gunzip -c backups/inovabens-AAAAMMDD-HHMM.sql.gz | sudo docker compose exec -T db psql -U inovabens -d inovabens
# Os arquivos ficam no disco da VM: copie-os periodicamente para fora (Object Storage, seu computador).
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p backups
arquivo="backups/inovabens-$(date +%Y%m%d-%H%M).sql.gz"
docker compose exec -T db pg_dump -U inovabens -d inovabens --clean --if-exists | gzip > "$arquivo"
find backups -name 'inovabens-*.sql.gz' -mtime +14 -delete
echo "$(date -Is) backup $arquivo ($(du -h "$arquivo" | cut -f1))"
