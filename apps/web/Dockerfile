# Build a partir da RAIZ do repositório:  docker build -f apps/web/Dockerfile -t inovabens-web .
FROM node:22-slim AS build
ENV PNPM_HOME=/pnpm PATH=/pnpm:$PATH NEXT_TELEMETRY_DISABLED=1
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY packages/shared/package.json packages/shared/
COPY apps/web/package.json apps/web/
RUN pnpm install --frozen-lockfile --filter @inovabens/web...
COPY packages/shared packages/shared
COPY apps/web apps/web
RUN pnpm --filter @inovabens/shared build && pnpm --filter @inovabens/web build

FROM node:22-slim AS run
ENV NODE_ENV=production PORT=3000 HOSTNAME=0.0.0.0 NEXT_TELEMETRY_DISABLED=1
RUN apt-get update && apt-get install -y --no-install-recommends tini && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=node:node /repo/apps/web/.next/standalone ./
COPY --from=build --chown=node:node /repo/apps/web/.next/static ./apps/web/.next/static
COPY --from=build --chown=node:node /repo/apps/web/public ./apps/web/public
USER node
EXPOSE 3000
# API_INTERNAL_URL é lida em tempo de execução pelo proxy /api/v1 (ex.: http://api:3001)
ENTRYPOINT ["tini", "--"]
CMD ["node", "apps/web/server.js"]
