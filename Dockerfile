# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TgCodexBridge.sln ./
COPY src/TgCodexBridge.Bot/TgCodexBridge.Bot.csproj src/TgCodexBridge.Bot/
COPY src/TgCodexBridge.Core/TgCodexBridge.Core.csproj src/TgCodexBridge.Core/
COPY src/TgCodexBridge.Infrastructure/TgCodexBridge.Infrastructure.csproj src/TgCodexBridge.Infrastructure/
COPY tests/TgCodexBridge.Tests/TgCodexBridge.Tests.csproj tests/TgCodexBridge.Tests/

RUN dotnet restore TgCodexBridge.sln

COPY . .
RUN dotnet publish src/TgCodexBridge.Bot/TgCodexBridge.Bot.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV DOTNET_EnableDiagnostics=0
ENV STATE_DIR=/data
ENV LOG_DIR=/data/logs

VOLUME ["/data"]

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl nodejs npm docker.io docker-compose-v2 \
    && npm install -g @openai/codex \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["/bin/sh", "-c", "test -f /data/heartbeat && test -f /data/state.db && test -d /data/logs"]

ENTRYPOINT ["dotnet", "TgCodexBridge.Bot.dll"]
