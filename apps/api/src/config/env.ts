import { z } from 'zod';

const schema = z.object({
  NODE_ENV: z.enum(['development', 'test', 'production']).default('development'),
  PORT: z.coerce.number().default(3001),
  DATABASE_URL: z.string().min(1),
  JWT_ACCESS_SECRET: z.string().min(32),
  JWT_REFRESH_SECRET: z.string().min(32),
  ACCESS_TTL_MIN: z.coerce.number().default(15),
  REFRESH_TTL_DAYS: z.coerce.number().default(7),
  CORS_ORIGIN: z.string().default('http://localhost:3000'),
  COOKIE_SECURE: z.enum(['true', 'false']).default('false').transform((v) => v === 'true'),
  S3_ENDPOINT: z.string().optional(),
  S3_REGION: z.string().default('auto'),
  S3_BUCKET: z.string().default('inovabens'),
  S3_ACCESS_KEY: z.string().optional(),
  S3_SECRET_KEY: z.string().optional(),
  S3_FORCE_PATH_STYLE: z.enum(['true', 'false']).default('true').transform((v) => v === 'true'),
  GOOGLE_CLIENT_ID: z.string().optional(),
  GOOGLE_CLIENT_SECRET: z.string().optional(),
  GOOGLE_REDIRECT_URI: z.string().optional(),
  GOOGLE_WEBHOOK_URL: z.string().optional(),
  TOKEN_ENC_KEY: z.string().length(64).default('0'.repeat(64)),
  WEB_URL: z.string().default('http://localhost:3000'),
  SEED_ADMIN_EMAIL: z.string().default('admin@inovabens.com.br'),
  SEED_ADMIN_PASSWORD: z.string().default('Trocar@123456'),
  // nº de proxies confiáveis à frente da API (Render: 1; Cloud Run atrás do web: 2)
  TRUST_PROXY: z.coerce.number().int().min(0).default(1),
  DISABLE_JOBS: z.enum(['true', 'false']).default('false').transform((v) => v === 'true'),
}).superRefine((v, ctx) => {
  // Em produção, recusar configurações inseguras (falha cedo, antes de atender requisições)
  if (v.NODE_ENV !== 'production') return;
  if (/^0+$/.test(v.TOKEN_ENC_KEY)) ctx.addIssue({ code: 'custom', path: ['TOKEN_ENC_KEY'], message: 'defina uma chave aleatória de 64 hex (openssl rand -hex 32)' });
  if (!v.COOKIE_SECURE) ctx.addIssue({ code: 'custom', path: ['COOKIE_SECURE'], message: 'deve ser true em produção (HTTPS)' });
  if (v.JWT_ACCESS_SECRET === v.JWT_REFRESH_SECRET) ctx.addIssue({ code: 'custom', path: ['JWT_REFRESH_SECRET'], message: 'deve ser diferente de JWT_ACCESS_SECRET' });
});

export type Env = z.infer<typeof schema>;
let cached: Env | undefined;

/** Lê e valida variáveis de ambiente (falha cedo, com mensagem clara). */
export function env(): Env {
  if (!cached) {
    const r = schema.safeParse(process.env);
    if (!r.success) {
      const msg = r.error.issues.map((i) => `${i.path.join('.')}: ${i.message}`).join('; ');
      throw new Error(`Configuração inválida: ${msg}`);
    }
    cached = r.data;
  }
  return cached;
}
export const resetEnvCache = () => { cached = undefined; };
