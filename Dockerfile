FROM node:22-slim AS web
WORKDIR /src
COPY web/package.json web/pnpm-lock.yaml web/
# Corepack reads packageManager from the working directory, which has no package.json, so pin the version here too.
RUN corepack enable && corepack prepare pnpm@10.32.1 --activate && pnpm -C web install --frozen-lockfile
COPY web web
RUN pnpm -C web build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# The web app is served from the server's wwwroot.
COPY --from=web /src/web/dist src/Kumbu.Server/wwwroot
RUN dotnet publish src/Kumbu.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=build /src/LICENSE /src/THIRD-PARTY-NOTICES.md ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Owned by the app user, so the named volumes mounted here start out writable for it.
RUN mkdir -p /recovery /models && chown app:app /recovery /models && chmod 700 /recovery
# `docker compose exec kumbu kumbu bootstrap-admin`: commands run through the same binary and configuration.
RUN printf '#!/bin/sh\nexec dotnet /app/Kumbu.Server.dll "$@"\n' > /usr/local/bin/kumbu && chmod 755 /usr/local/bin/kumbu
USER app
ENTRYPOINT ["dotnet", "Kumbu.Server.dll"]
