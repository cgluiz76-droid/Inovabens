/** @type {import('next').NextConfig} */
export default {
  output: 'standalone',
  reactStrictMode: true,
  poweredByHeader: false,
  // monorepo: inclui o pacote compartilhado no trace do standalone
  outputFileTracingRoot: new URL('../../', import.meta.url).pathname,
};
