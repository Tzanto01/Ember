# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
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
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0@sha256:bc0d6653979efba491ab3d6f589d691f96c309c3c3ef81325efa6978e7b7f242 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=web /web/dist/ember-web/browser /app/wwwroot
ENV Ember__WebRoot=/app/wwwroot
RUN mkdir /keys && chown $APP_UID /keys
USER $APP_UID
ENTRYPOINT ["dotnet", "Ember.Api.dll"]
