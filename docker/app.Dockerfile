# syntax=docker/dockerfile:1

# ---- Restore ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS restore
WORKDIR /src
COPY . .
RUN dotnet restore "src/CSweet.App/CSweet.App.csproj"

# ---- Publish ----
FROM restore AS publish
RUN dotnet publish "src/CSweet.App/CSweet.App.csproj" -c Release -o /app/publish --no-restore

# ---- Runtime ----
FROM nginx:1.27-alpine AS final
COPY ["docker/nginx.conf", "/etc/nginx/conf.d/default.conf"]
COPY --from=publish /app/publish/wwwroot /usr/share/nginx/html

EXPOSE 8080
