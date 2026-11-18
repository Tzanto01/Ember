# Build context: ops/ (docker compose -f ops/compose.yml build).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Ember.Ops/Ember.Ops.csproj src/Ember.Ops/
RUN dotnet restore src/Ember.Ops/Ember.Ops.csproj
COPY src/ src/
RUN dotnet publish src/Ember.Ops/Ember.Ops.csproj -c Release --no-restore -o /app/publish

FROM oven/bun:1.3.14 AS web
WORKDIR /web
COPY web/package.json web/bun.lock ./
RUN bun install --frozen-lockfile
COPY web/ ./
RUN bun --bun run build

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=web /web/dist /app/wwwroot
ARG BUILD_SHA=""
ENV Ops__BuildSha=$BUILD_SHA \
    Ops__WebRoot=/app/wwwroot
USER $APP_UID
ENTRYPOINT ["dotnet", "Ember.Ops.dll"]
