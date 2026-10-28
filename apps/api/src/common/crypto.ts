import { createCipheriv, createDecipheriv, createHmac, randomBytes } from 'node:crypto';
import { env } from '../config/env';

const chave = () => Buffer.from(env().TOKEN_ENC_KEY, 'hex');

/** AES-256-GCM: iv(12) | tag(16) | cifrado, em base64. Usado p/ tokens do Google e segredo TOTP. */
export function encrypt(texto: string): string {
  const iv = randomBytes(12);
  const c = createCipheriv('aes-256-gcm', chave(), iv);
  const ct = Buffer.concat([c.update(texto, 'utf8'), c.final()]);
  return Buffer.concat([iv, c.getAuthTag(), ct]).toString('base64');
}
export function decrypt(b64: string): string {
  const buf = Buffer.from(b64, 'base64');
  const d = createDecipheriv('aes-256-gcm', chave(), buf.subarray(0, 12));
  d.setAuthTag(buf.subarray(12, 28));
  return Buffer.concat([d.update(buf.subarray(28)), d.final()]).toString('utf8');
}
/** HMAC-SHA256 hex: refresh tokens e tokens de reset só são guardados assim, nunca em claro. */
export const hmac = (v: string) => createHmac('sha256', env().JWT_REFRESH_SECRET).update(v).digest('hex');
export const tokenAleatorio = (bytes = 48) => randomBytes(bytes).toString('base64url');
