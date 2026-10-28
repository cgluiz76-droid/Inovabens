-- 2FA: último intervalo de 30 s aceito por usuário, para o mesmo código não valer duas vezes (reuso/replay).
-- Só a versão .NET usa a coluna; a versão Node a ignora.
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS totp_ultimo_passo bigint;
