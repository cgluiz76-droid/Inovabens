#!/usr/bin/env bash
# Prepara uma VM Ubuntu da Oracle Cloud e sobe o Inovabens (PostgreSQL + aplicação + Caddy).
# Uso, dentro da VM, com o repositório já copiado (git clone ou scp):
#   DOMINIO=inovabens.exemplo.com.br SEED_ADMIN_EMAIL=voce@empresa.com bash infra/oracle/instalar.sh
# Pode ser executado de novo para atualizar: mantém o .env e o banco, só reconstrói a aplicação.
set -euo pipefail
cd "$(dirname "$0")"

# 1. Docker
if ! command -v docker >/dev/null; then
  curl -fsSL https://get.docker.com | sudo sh
  sudo usermod -aG docker "$USER"
fi

# 2. Firewall da VM: a imagem Ubuntu da Oracle rejeita tudo além da 22. Abre 80/443 antes da regra REJECT.
# (Libere também 80 e 443 TCP na Security List da VCN, no painel da Oracle.)
for p in 80 443; do
  if ! sudo iptables -C INPUT -p tcp --dport "$p" -m state --state NEW -j ACCEPT 2>/dev/null; then
    n=$(sudo iptables -L INPUT --line-numbers | awk '/REJECT/ {print $1; exit}')
    sudo iptables -I INPUT "${n:-1}" -p tcp --dport "$p" -m state --state NEW -j ACCEPT
  fi
done
sudo iptables -C INPUT -p udp --dport 443 -j ACCEPT 2>/dev/null \
  || sudo iptables -I INPUT "$(sudo iptables -L INPUT --line-numbers | awk '/REJECT/ {print $1; exit}')" -p udp --dport 443 -j ACCEPT
command -v netfilter-persistent >/dev/null && sudo netfilter-persistent save

# 3. .env com segredos novos (só na primeira vez)
if [ ! -f .env ]; then
  : "${DOMINIO:?defina DOMINIO (ex.: inovabens.exemplo.com.br ou <IP>.sslip.io)}"
  : "${SEED_ADMIN_EMAIL:?defina SEED_ADMIN_EMAIL}"
  senha_admin="${SEED_ADMIN_PASSWORD:-$(openssl rand -base64 18)}"
  sed -e "s|^DOMINIO=.*|DOMINIO=$DOMINIO|" \
      -e "s|^SEED_ADMIN_EMAIL=.*|SEED_ADMIN_EMAIL=$SEED_ADMIN_EMAIL|" \
      -e "s|^SEED_ADMIN_PASSWORD=.*|SEED_ADMIN_PASSWORD=$senha_admin|" \
      -e "s|^DB_PASSWORD=.*|DB_PASSWORD=$(openssl rand -hex 24)|" \
      -e "s|^TOKEN_ENC_KEY=.*|TOKEN_ENC_KEY=$(openssl rand -hex 32)|" \
      -e "s|^JWT_REFRESH_SECRET=.*|JWT_REFRESH_SECRET=$(openssl rand -hex 32)|" \
      .env.example > .env
  chmod 600 .env
  echo "Criado infra/oracle/.env. Senha inicial do administrador ($SEED_ADMIN_EMAIL): $senha_admin"
  echo "Guarde uma cópia do .env fora da VM: sem a TOKEN_ENC_KEY, 2FA e conexões Google ficam ilegíveis."
fi

# 4. Sobe/atualiza (sudo: o grupo docker só vale no próximo login)
sudo docker compose up -d --build
sudo docker image prune -f >/dev/null

# 5. Backup diário do banco às 3h
linha="0 3 * * * $(pwd)/backup.sh >> $(pwd)/backup.log 2>&1"
( sudo crontab -l 2>/dev/null | grep -vF "$(pwd)/backup.sh"; echo "$linha" ) | sudo crontab -
chmod +x backup.sh

echo "Pronto. Confira: https://$(grep '^DOMINIO=' .env | cut -d= -f2)/api/v1/health"
