# Base images by digest, so a re-tagged image can't change what is built; the tag before the digest says which
# image it is and is what Dependabot follows (.github/dependabot.yml) when it proposes a new digest.
# node:26-slim
FROM node:26-slim@sha256:36793bcbb160a6a16cede1e62b93f84e26b78a972a8a4c0b879c1c12f22761ec AS web
WORKDIR /src
COPY web/package.json web/pnpm-lock.yaml web/
# Node 25+ ships no corepack, so pnpm comes from npm, at the version web/package.json's packageManager names.
RUN npm install -g pnpm@10.32.1 && pnpm -C web install --frozen-lockfile
COPY web web
# One version for the app and the server (set by the release workflow, .github/workflows/docker.yml): the web app
# compares its own with the server's X-Kumbu-Version and reloads into a newer one. 0.0.0-dev turns that off.
ARG VERSION=0.0.0-dev
ARG GIT_SHA=""
RUN KUMBU_VERSION="$VERSION" KUMBU_GIT_SHA="$GIT_SHA" pnpm -C web build

# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:afd0ccff9d306812a69ef0f47458376b6acb544ae866cfc4b8d28ff0eef35ea7 AS build
WORKDIR /src
COPY . .
# The web app is served from the server's wwwroot.
COPY --from=web /src/web/dist src/Kumbu.Server/wwwroot
# The same version as the web stage's, so app and server agree: the SDK makes the informational version
# "$VERSION+$GIT_SHA" (BuildVersion.cs), just "$VERSION" without a commit (no .git in the build context).
ARG VERSION=0.0.0-dev
ARG GIT_SHA=""
# Locked: exactly the packages in packages.lock.json, or the build fails.
RUN dotnet restore src/Kumbu.Server --locked-mode && \
    dotnet publish src/Kumbu.Server -c Release -o /app --no-restore -p:Version="$VERSION" -p:SourceRevisionId="$GIT_SHA"

# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
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
