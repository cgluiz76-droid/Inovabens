# Build a partir da RAIZ do repositório:  docker build -f apps/api/Dockerfile -t inovabens-api .
FROM node:22-slim AS build
ENV PNPM_HOME=/pnpm PATH=/pnpm:$PATH
RUN corepack enable
WORKDIR /repo
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml ./
COPY packages/shared/package.json packages/shared/
COPY apps/api/package.json apps/api/
RUN pnpm install --frozen-lockfile --filter @inovabens/api...
COPY packages/shared packages/shared
COPY apps/api apps/api
RUN pnpm --filter @inovabens/shared build && pnpm --filter @inovabens/api build
# pacote autossuficiente só com dependências de produção
RUN pnpm --filter @inovabens/api deploy --prod --legacy /out && cp -r apps/api/dist /out/dist && cp -r apps/api/drizzle /out/drizzle

FROM node:22-slim AS run
ENV NODE_ENV=production PORT=3001
RUN apt-get update && apt-get install -y --no-install-recommends tini && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build --chown=node:node /out ./
USER node
EXPOSE 3001
HEALTHCHECK --interval=30s --timeout=5s --start-period=40s CMD node -e "fetch('http://127.0.0.1:'+process.env.PORT+'/api/v1/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
# aplica migrations, garante dados base/admin inicial e sobe a API
ENTRYPOINT ["tini", "--"]
CMD ["npm", "run", "start:prod"]
