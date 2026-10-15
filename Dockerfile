# Valmedo - Appointment: one image serves the API and the web app on port 8080.
#
#   docker build --target app      -t valmedo .          # the application
#   docker build --target migrator -t valmedo-migrator . # applies database migrations once, then exits
#
# See docs/deployment.md.

# ---- 1. Web app (React + Vite) ----
FROM node:22-alpine AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# ---- 2. API publish and migration bundle ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY dotnet-tools.json ./
RUN dotnet tool restore
COPY src/SmartQueue.Api/SmartQueue.Api.csproj src/SmartQueue.Api/
RUN dotnet restore src/SmartQueue.Api/SmartQueue.Api.csproj
COPY src/ src/
RUN dotnet publish src/SmartQueue.Api/SmartQueue.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false
COPY --from=web /src/web/dist /app/wwwroot
RUN dotnet ef migrations bundle --project src/SmartQueue.Api/SmartQueue.Api.csproj --configuration Release \
      --self-contained --target-runtime linux-x64 --output /migrator/efbundle --force

# ---- 3a. Migrator: `docker run --rm -e ConnectionStrings__Default=... valmedo-migrator` ----
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrator
WORKDIR /migrator
COPY --from=build /migrator/efbundle ./efbundle
USER $APP_UID
ENTRYPOINT ["/bin/sh", "-c", "exec ./efbundle --connection \"$ConnectionStrings__Default\""]

# ---- 3b. Application (default target) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS app
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartQueue.Api.dll"]
