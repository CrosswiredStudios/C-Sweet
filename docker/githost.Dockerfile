# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/CSweet.GitHost/CSweet.GitHost.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends git git-lfs curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/repositories /trust \
    && printf 'csweet-local-git' > /data/repositories/.csweet-git-store \
    && chown -R $APP_UID:0 /data /trust
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "CSweet.GitHost.dll"]
