# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
WORKDIR /src
COPY src/Ember.Core/Ember.Core.csproj src/Ember.Core/
COPY src/Ember.Api/Ember.Api.csproj src/Ember.Api/
RUN dotnet restore src/Ember.Api/Ember.Api.csproj
COPY src/ src/
RUN dotnet publish src/Ember.Api/Ember.Api.csproj \
    -c Release \
    --no-restore \
    -o /app/publish

# ── Web build stage ───────────────────────────────────────────────────────────
FROM oven/bun:1.3.14@sha256:e10577f0db68676a7024391c6e5cb4b879ebd17188ab750cf10024a6d700e5c4 AS web
WORKDIR /web
COPY web/package.json web/bun.lock ./
RUN bun install --frozen-lockfile
COPY web/ ./
COPY docs/contracts/fixtures/ /docs/contracts/fixtures/
ARG VITE_PUBLIC_ORIGIN=""
ENV VITE_PUBLIC_ORIGIN=$VITE_PUBLIC_ORIGIN
RUN bun --bun run build

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=web /web/dist/ember-web/browser /app/wwwroot
ENV Ember__WebRoot=/app/wwwroot
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
ENTRYPOINT ["dotnet", "Ember.Api.dll"]
