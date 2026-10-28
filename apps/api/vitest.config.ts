import { resolve } from 'node:path';
import { defineConfig } from 'vitest/config';
import swc from 'unplugin-swc';

export default defineConfig({
  test: { globals: true, environment: 'node', include: ['src/**/*.spec.ts', 'test/**/*.spec.ts'], testTimeout: 60000, hookTimeout: 120000 },
  // testes usam o código-fonte do pacote compartilhado (sem exigir build prévio)
  resolve: { alias: { '@inovabens/shared': resolve(__dirname, '../../packages/shared/src/index.ts') } },
  plugins: [swc.vite({ module: { type: 'es6' } })],
});
