# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PosSync.Core/PosSync.Core.csproj src/PosSync.Core/
COPY src/PosSync.Server/PosSync.Server.csproj src/PosSync.Server/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/PosSync.Server/PosSync.Server.csproj

COPY .editorconfig ./
COPY src/PosSync.Core/ src/PosSync.Core/
COPY src/PosSync.Server/ src/PosSync.Server/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/PosSync.Server/PosSync.Server.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="offline-pos-sync-server" \
      org.opencontainers.image.source="https://github.com/hekimcan206/offline-pos-sync" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PosSync.Server.dll"]
