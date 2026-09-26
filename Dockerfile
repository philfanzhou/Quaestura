# ========== Stage 1: Build Admin Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://mirrors.huaweicloud.com/repository/npm/
ENV npm_config_registry=${NPM_REGISTRY}
ENV npm_config_replace_registry_host=always
WORKDIR /app
COPY frontend/package*.json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/Common/Quaestura.Common.csproj src/Common/
COPY src/Consul/Quaestura.Consul.csproj src/Consul/
COPY src/Database/Quaestura.Database.csproj src/Database/
COPY src/Domain/Quaestura.Domain.csproj src/Domain/
COPY src/Service/Quaestura.Service.csproj src/Service/
COPY src/Host/Quaestura.Host.csproj src/Host/

RUN dotnet restore "src/Host/Quaestura.Host.csproj"

COPY src/ src/

# Inject the admin frontend build artifacts into the Host's wwwroot
COPY --from=frontend-build /app/dist src/Host/wwwroot

RUN dotnet publish "src/Host/Quaestura.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ========== Stage 3: Final Runtime Image ==========
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5007
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Quaestura.Host.dll"]
