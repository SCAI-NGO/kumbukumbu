FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Kumbu.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
# Owned by the app user, so the named volumes mounted here start out writable for it.
RUN mkdir -p /recovery /models && chown app:app /recovery /models && chmod 700 /recovery
USER app
ENTRYPOINT ["dotnet", "Kumbu.Server.dll"]
