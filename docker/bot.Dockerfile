# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:11.0.100@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS build
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
