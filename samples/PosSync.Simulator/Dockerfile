# syntax=docker/dockerfile:1
# Build context: the repository root (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PosSync.Core/PosSync.Core.csproj src/PosSync.Core/
COPY src/PosSync.Device/PosSync.Device.csproj src/PosSync.Device/
COPY src/PosSync.Chaos/PosSync.Chaos.csproj src/PosSync.Chaos/
COPY samples/PosSync.Simulator/PosSync.Simulator.csproj samples/PosSync.Simulator/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore samples/PosSync.Simulator/PosSync.Simulator.csproj

COPY .editorconfig ./
COPY src/PosSync.Core/ src/PosSync.Core/
COPY src/PosSync.Device/ src/PosSync.Device/
COPY src/PosSync.Chaos/ src/PosSync.Chaos/
COPY samples/PosSync.Simulator/ samples/PosSync.Simulator/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish samples/PosSync.Simulator/PosSync.Simulator.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# The terminals keep SQLite files (WAL mode) under /tmp, so this image keeps a writable /tmp; it still runs as non-root.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS runtime
LABEL org.opencontainers.image.title="offline-pos-sync-simulator" \
      org.opencontainers.image.source="https://github.com/hekimcan206/offline-pos-sync" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
ENV DATA_DIR=/tmp/possync-sim
ENTRYPOINT ["dotnet", "PosSync.Simulator.dll"]
