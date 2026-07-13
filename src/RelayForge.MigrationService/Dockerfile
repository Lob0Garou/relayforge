FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/RelayForge.MigrationService/RelayForge.MigrationService.csproj && dotnet publish src/RelayForge.MigrationService/RelayForge.MigrationService.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "RelayForge.MigrationService.dll"]
