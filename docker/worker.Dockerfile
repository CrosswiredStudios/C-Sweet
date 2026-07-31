# syntax=docker/dockerfile:1

# ---- Restore ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS restore
WORKDIR /src
COPY . .
RUN dotnet restore "src/CSweet.WorkerHost/CSweet.WorkerHost.csproj"

# ---- Publish ----
FROM restore AS publish
RUN dotnet publish "src/CSweet.WorkerHost/CSweet.WorkerHost.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends docker.io git \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/csweet/agents/sources /var/lib/csweet/agents/packages \
    && chown -R $APP_UID:0 /var/lib/csweet
WORKDIR /app
COPY --from=publish /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "CSweet.WorkerHost.dll"]
