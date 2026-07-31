# syntax=docker/dockerfile:1

# ---- Restore ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS restore
WORKDIR /src
COPY . .
RUN dotnet restore "src/CSweet.Migrator/CSweet.Migrator.csproj"

# ---- Publish ----
FROM restore AS publish
RUN dotnet publish "src/CSweet.Migrator/CSweet.Migrator.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "CSweet.Migrator.dll"]
