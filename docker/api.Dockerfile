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
FROM oven/bun:1.4.5@sha256:3aa040e5445efa09d75bb428a64ba50db13e2b3ac9b6b0c7f145615f62b2ee18 AS web
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
