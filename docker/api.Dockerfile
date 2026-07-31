# syntax=docker/dockerfile:1

# ---- Restore ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS restore
WORKDIR /src
COPY . .
RUN dotnet restore "src/CSweet.Api/CSweet.Api.csproj"

# ---- Publish ----
FROM restore AS publish
RUN dotnet publish "src/CSweet.Api/CSweet.Api.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /state \
    && chown -R $APP_UID:$APP_UID /state

USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
ENV CSweet__Secrets__FilePath=/state/provider-secrets.json
EXPOSE 8080

ENTRYPOINT ["dotnet", "CSweet.Api.dll"]
