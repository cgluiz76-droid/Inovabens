# Publicação em nuvem

O sistema é **um contêiner** (`dotnet/Dockerfile`, build a partir da pasta `dotnet/`) mais um **PostgreSQL 16**; fotos e documentos ficam num **drive compartilhado do Google Drive**. O `render.yaml` sobe tudo no Render; os mesmos passos valem para Railway, Fly.io, Google Cloud Run etc.

## 1. Pré-requisitos
1. **Banco**: PostgreSQL 16 gerenciado (o blueprint cria um no Render).
2. **Arquivos**: drive compartilhado do Google Workspace + conta de serviço do Google Cloud (Google Drive API ativada) como **Administrador de conteúdo** do drive. Defina `GOOGLE_DRIVE_ID` (ID do drive, trecho da URL depois de `/folders/`) e a chave JSON da conta de serviço: `GOOGLE_DRIVE_CREDENCIAIS` (caminho do arquivo) ou `GOOGLE_DRIVE_CREDENCIAIS_JSON` (o conteúdo). Sem isso o sistema funciona, mas o envio de fotos/documentos fica desligado. Detalhes no [README](README.md#fotos-e-documentos-no-google-drive).
3. **Segredos** (sem eles a aplicação **não sobe** em produção):
   - `TOKEN_ENC_KEY`: `openssl rand -hex 32`. Cifra os tokens do Google e os segredos de 2FA.
   - `JWT_REFRESH_SECRET`: 32+ caracteres aleatórios, diferente da `TOKEN_ENC_KEY`. Assina o `state` do OAuth e o token do webhook do Google.
   - `SEED_ADMIN_PASSWORD`: senha forte do administrador inicial (a senha padrão é recusada).

## 2. Render (blueprint)
1. Suba o repositório no GitHub.
2. Render → **New → Blueprint** → selecione o repositório (usa `render.yaml`).
3. Preencha as variáveis `sync:false` do serviço `inovabens-web`: `TOKEN_ENC_KEY`, `JWT_REFRESH_SECRET`, `WEB_URL` (URL pública, ex.: `https://inovabens-web.onrender.com`), `SEED_ADMIN_EMAIL`, `SEED_ADMIN_PASSWORD`, `GOOGLE_DRIVE_ID` e `GOOGLE_DRIVE_CREDENCIAIS_JSON`.
4. A cada subida a aplicação aplica as migrações pendentes, garante perfis/categorias/estágios padrão e cria o administrador inicial (só se ainda não houver usuários). Verifique `https://SEU-SITE/api/v1/health`.
5. Mantenha **1 instância**: os jobs agendados rodam no próprio serviço (alertas, lançamentos mensais, índices do Banco Central, sincronização Google). Para escalar, defina `DISABLE_JOBS=true` em todas as instâncias menos uma. As sessões já funcionam em várias instâncias (as chaves ficam no banco), mas o Blazor Server precisa de **afinidade de sessão** (sticky sessions) no balanceador.

### Migrando da antiga versão Node no Render
A versão Node tinha dois serviços (`inovabens-api` e `inovabens-web`). O blueprint novo reaproveita o nome `inovabens-web`, então o endereço público e a configuração do Google não mudam.
1. **Antes de sincronizar o blueprint**, copie do `inovabens-api` os valores de `TOKEN_ENC_KEY` e `JWT_REFRESH_SECRET`. A `TOKEN_ENC_KEY` precisa ser **a mesma**: sem ela os segredos de 2FA e as conexões com o Google já gravados ficam ilegíveis.
2. Sincronize o blueprint e preencha as variáveis no `inovabens-web` (as do Google também vêm do `inovabens-api`).
3. O banco é o mesmo: na primeira subida a versão .NET reconhece as migrações já aplicadas pelo Node e aplica só as novas. Senhas, 2FA e dados continuam valendo.
4. Confirme o acesso e apague o serviço `inovabens-api` no painel (o blueprint não remove serviços sozinho).

## 3. Google Calendar (opcional)
1. Google Cloud Console → crie um projeto → **APIs e serviços → Biblioteca** → ative *Google Calendar API*.
2. **Tela de consentimento OAuth** (escopo `https://www.googleapis.com/auth/calendar.events`) e **Credenciais → ID do cliente OAuth (aplicativo da Web)**.
3. URI de redirecionamento autorizado: `https://SEU-SITE/api/v1/integracoes/google/callback`.
4. Defina `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET`, `GOOGLE_REDIRECT_URI` (a mesma URI) e, para sincronização quase em tempo real, `GOOGLE_WEBHOOK_URL=https://SEU-SITE/api/v1/webhooks/google/calendar` (HTTPS público). Sem webhook a sincronização ocorre a cada 10 minutos.
5. Em **Agenda → Conectar Google Calendar** cada usuário autoriza a própria conta.

Comportamento: compromissos criados no sistema vão para o Google; alterações em qualquer lado são refletidas (vence a atualização mais recente). Eventos criados direto no Google só entram no sistema se o título/descrição citarem o código do imóvel, ex.: `Visita #IMV-000012`.

## 4. Checklist de produção
- [ ] Trocar a senha do administrador inicial e ativar 2FA (*Minha conta*).
- [ ] HTTPS no domínio (Render e Cloud Run já fornecem). O cookie de sessão só trafega por HTTPS em produção.
- [ ] Backup automático do PostgreSQL ativado no provedor (inclui `audit_log` e as chaves de sessão).
- [ ] Drive compartilhado sem link público; a chave JSON da conta de serviço só no servidor/segredos (o sistema entrega os arquivos conferindo o acesso).
- [ ] Criar os usuários reais e atribuir perfis (Administrador, Corretor, Financeiro) em *Administração*.
- [ ] Sincronizar os índices (*Administração → Configurações → Índices econômicos*) ou aguardar o job mensal.
- [ ] Atrás de mais de um proxy, ajuste `TRUST_PROXY` (quantos há na frente; padrão 1) para o limite de tentativas de login usar o IP real.

## 5. Atualizações
Cada deploy aplica as migrações pendentes e os dados-base na subida (idempotente, com lock entre instâncias). Para rodar só essa parte como passo de release: `dotnet Inovabens.Web.dll migrar`. Novas alterações de esquema: um novo arquivo `dotnet/src/Inovabens.Core/Dados/Migracoes/NNNN_descricao.sql` (o próximo número). Nunca edite um script já aplicado.

## 6. Google Cloud (Cloud Run + Cloud SQL)

Alternativa ao Render, tudo no Google. Os arquivos continuam no **drive compartilhado do Google Drive** e o calendário no **Google Calendar**.

| Peça | Serviço |
|---|---|
| Sistema | Cloud Run (`inovabens-web`) |
| Banco | Cloud SQL for PostgreSQL 16 (`db-f1-micro`, backup diário, proteção contra exclusão) |
| Fotos/documentos | Google Drive (drive compartilhado; chave da conta de serviço no Secret Manager) |
| Segredos | Secret Manager |

**Pré-requisito:** um projeto no Google Cloud com faturamento ativo.

```bash
# no Cloud Shell (console.cloud.google.com → ícone >_) com o código do projeto descompactado
export PROJECT_ID=seu-projeto
export SEED_ADMIN_EMAIL=voce@suaempresa.com
export GOOGLE_DRIVE_ID=0A...                # drive compartilhado dos arquivos
export DRIVE_CREDENCIAIS=chave-drive.json   # chave JSON da conta de serviço (só na 1ª vez)
bash infra/gcp/deploy-gcp.sh
```
O script é idempotente: rode de novo para publicar uma nova versão (não recria banco nem segredos). Ele imprime a URL do site e a senha inicial do administrador; troque-a no primeiro acesso.

Pontos de atenção:
- O serviço roda com **1 instância sempre ligada e CPU sempre alocada** (`--min-instances=1 --no-cpu-throttling`), pois os jobs agendados rodam nele. Isso é o principal custo fixo, além do Cloud SQL; consulte a calculadora de preços do Google.
- Blazor Server mantém uma conexão WebSocket por tela aberta: o script usa `--timeout=3600` e `--session-affinity`.
- O banco é acessado pelo socket do Cloud SQL (`DATABASE_URL` com `?host=/cloudsql/...`), formato que a aplicação entende.
- O domínio padrão é `*.run.app`; para domínio próprio use *Cloud Run → Gerenciar domínios personalizados* no serviço `inovabens-web` e atualize `WEB_URL`/`GOOGLE_REDIRECT_URI`/`GOOGLE_WEBHOOK_URL`.
- Quem já tinha publicado a versão Node com este script: o serviço `inovabens-web` passa a rodar a versão .NET (mesmo endereço e segredos) e o script avisa como apagar o antigo `inovabens-api`.
- O script **não foi executado contra uma conta real do Google Cloud** por quem o escreveu; o primeiro deploy pode exigir pequenos ajustes (por exemplo, permissões da conta que o executa: Proprietário ou Editor do projeto).

## 7. Oracle Cloud (VM Always Free + Docker Compose)
Tudo em uma VM gratuita: PostgreSQL em contêiner, a aplicação e o Caddy (HTTPS automático). Arquivos em `infra/oracle/`.
1. **Criar a VM**: Compute → Instances → *Create*. Imagem **Ubuntu 24.04**, shape **VM.Standard.A1.Flex** (ARM, até 4 OCPU / 24 GB grátis; 2 OCPU / 12 GB bastam). Baixe a chave SSH. Se aparecer *Out of capacity*, tente outro Availability Domain ou mais tarde.
2. **Rede**: na Security List da VCN da VM, adicione regras de entrada TCP 80 e 443 (origem `0.0.0.0/0`).
3. **Domínio**: aponte um registro A para o IP público da VM. Sem domínio, use `<IP>.sslip.io`.
4. **Instalar** (na VM, via SSH):
   ```bash
   git clone <url-do-repositório> inovabens && cd inovabens
   DOMINIO=inovabens.exemplo.com.br SEED_ADMIN_EMAIL=voce@empresa.com bash infra/oracle/instalar.sh
   ```
   O script instala o Docker, abre 80/443 no firewall da VM, gera `infra/oracle/.env` com segredos novos, sobe os contêineres e agenda o backup diário. **Guarde uma cópia do `.env` fora da VM.**
5. **Atualizar**: `git pull && bash infra/oracle/instalar.sh` (mantém `.env` e banco).
6. **Backup**: `infra/oracle/backup.sh` grava `pg_dump` em `infra/oracle/backups/` (14 dias). Copie os arquivos para fora da VM com frequência.
7. **Fotos/documentos** (Google Drive): copie a chave JSON da conta de serviço para `infra/oracle/google-drive.json` na VM (ex.: `scp`), coloque `GOOGLE_DRIVE_ID=...` no `.env` e rode `sudo docker compose up -d` em `infra/oracle/`. Esse arquivo nunca vai para o Git.
8. A Oracle pode recuperar VMs Always Free ociosas. Converter a conta para *Pay As You Go* evita isso e continua sem custo dentro dos limites grátis.
