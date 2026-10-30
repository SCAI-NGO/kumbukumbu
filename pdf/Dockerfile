# The PDF sidecar: parses untrusted PDFs and decodes untrusted images, away from the vault keys.
# Base images by digest; the tag above each digest says which image it is and is what Dependabot follows.

# node:22-bookworm-slim
FROM node:22-bookworm-slim@sha256:4b150b48b4116e2a22f2c39cc27025fbf4f153e2078fd3b14d159fdb5c7279da AS build
WORKDIR /app
# pnpm at the version package.json's packageManager names.
RUN corepack enable
COPY package.json pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY tsconfig.json ./
COPY src src
RUN pnpm build && pnpm prune --prod

# node:22-bookworm-slim
FROM node:22-bookworm-slim@sha256:4b150b48b4116e2a22f2c39cc27025fbf4f153e2078fd3b14d159fdb5c7279da
ENV NODE_ENV=production PORT=8090
WORKDIR /app
# Owned by root and read-only to the node user, which runs the service.
COPY --from=build /app/package.json ./
COPY --from=build /app/node_modules node_modules
COPY --from=build /app/dist dist
# The licence and the third-party notices from the repository's root, a named build context: docker build
# --build-context root=. pdf (compose.build.yaml and the image workflow pass it). Without it, `root` would be taken
# for an image to pull.
COPY --from=root LICENSE THIRD-PARTY-NOTICES.md ./
USER node
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:' + (process.env.PORT || 8090) + '/healthz').then((r) => process.exit(r.ok ? 0 : 1), () => process.exit(1))"]
CMD ["node", "dist/server.js"]
