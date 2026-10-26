# Base images by digest, so a re-tagged image can't change what is built; the tag before the digest says which
# image it is and is what Dependabot follows (.github/dependabot.yml) when it proposes a new digest.
# node:22-slim
FROM node:22-slim@sha256:de170b39439eb0e5a3444ad7d60b015614a381e10fecae2e30ec9e735122f38d AS web
WORKDIR /src
COPY web/package.json web/pnpm-lock.yaml web/
# Corepack reads packageManager from the working directory, which has no package.json, so pin the version here too.
RUN corepack enable && corepack prepare pnpm@10.32.1 --activate && pnpm -C web install --frozen-lockfile
COPY web web
RUN pnpm -C web build

# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:afd0ccff9d306812a69ef0f47458376b6acb544ae866cfc4b8d28ff0eef35ea7 AS build
WORKDIR /src
COPY . .
# The web app is served from the server's wwwroot.
COPY --from=web /src/web/dist src/Kumbu.Server/wwwroot
# Locked: exactly the packages in packages.lock.json, or the build fails.
RUN dotnet restore src/Kumbu.Server --locked-mode && dotnet publish src/Kumbu.Server -c Release -o /app --no-restore

# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d247d17dd429d96580eeb96cfce220c1089d860dbbf9f94d599ca64cf3604fe4
ARG VERSION=0.0.0-dev
ARG GIT_SHA=""
ARG BUILT_AT=""
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/LICENSE /src/THIRD-PARTY-NOTICES.md ./
# Version marker: set by the release workflow (.github/workflows/docker.yml), `0.0.0-dev` in local builds.
RUN printf '%s\n' "$VERSION ($GIT_SHA, $BUILT_AT)" > /app/VERSION
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Owned by the app user, so the named volumes mounted here start out writable for it.
RUN mkdir -p /recovery /deletions /models && chown app:app /recovery /deletions /models && chmod 700 /recovery /deletions
# `docker compose exec kumbu kumbu bootstrap-admin`: commands run through the same binary and configuration.
RUN printf '#!/bin/sh\nexec dotnet /app/Kumbu.Server.dll "$@"\n' > /usr/local/bin/kumbu && chmod 755 /usr/local/bin/kumbu
USER app
ENTRYPOINT ["dotnet", "Kumbu.Server.dll"]
