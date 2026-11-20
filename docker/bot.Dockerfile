# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src
COPY src/Ember.Core/Ember.Core.csproj src/Ember.Core/
COPY src/Ember.Bot/Ember.Bot.csproj src/Ember.Bot/
RUN dotnet restore src/Ember.Bot/Ember.Bot.csproj
COPY src/ src/
RUN dotnet publish src/Ember.Bot/Ember.Bot.csproj \
    -c Release \
    --no-restore \
    -o /app/publish

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:10.0.15@sha256:64a538092439382d055c87c0f49092efb8bca883c736c54ae523da7fd8e02d18 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ember.Bot.dll"]
