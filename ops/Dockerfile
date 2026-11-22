# Build context: ops/ (docker compose -f ops/compose.yml build).
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY src/Ember.Ops/Ember.Ops.csproj src/Ember.Ops/
RUN dotnet restore src/Ember.Ops/Ember.Ops.csproj
COPY src/ src/
RUN dotnet publish src/Ember.Ops/Ember.Ops.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ARG BUILD_SHA=""
ENV Ops__BuildSha=$BUILD_SHA
USER $APP_UID
ENTRYPOINT ["dotnet", "Ember.Ops.dll"]
