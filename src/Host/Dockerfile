# ========== Stage 1: Build Admin Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://mirrors.huaweicloud.com/repository/npm/
ENV npm_config_registry=${NPM_REGISTRY}
ENV npm_config_replace_registry_host=always
WORKDIR /app
COPY src/questionbank_portal/frontend/package*.json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY src/questionbank_portal/frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/services/ruoyu.questionBank/src/Database/Ruoyu.Study.QuestionBank.Database.csproj src/services/ruoyu.questionBank/src/Database/
COPY src/services/ruoyu.questionBank/src/Domain/Ruoyu.Study.QuestionBank.Domain.csproj src/services/ruoyu.questionBank/src/Domain/
COPY src/services/ruoyu.questionBank/src/Service/Ruoyu.Study.QuestionBank.Service.csproj src/services/ruoyu.questionBank/src/Service/
COPY src/services/ruoyu.questionBank/src/Host/Ruoyu.Study.QuestionBank.Host.csproj src/services/ruoyu.questionBank/src/Host/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj src/services/ruoyu.common/src/Ruoyu.Study.Common/

RUN dotnet restore "src/services/ruoyu.questionBank/src/Host/Ruoyu.Study.QuestionBank.Host.csproj"

COPY src/services/ruoyu.questionBank/src/ src/services/ruoyu.questionBank/src/
COPY src/services/ruoyu.common/src/ src/services/ruoyu.common/src/

# Inject the admin frontend build artifacts into the Host's wwwroot
COPY --from=frontend-build /app/dist src/services/ruoyu.questionBank/src/Host/wwwroot

RUN dotnet publish "src/services/ruoyu.questionBank/src/Host/Ruoyu.Study.QuestionBank.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Final Runtime Image ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 5007
ENV ASPNETCORE_URLS=http://+:5007
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Ruoyu.Study.QuestionBank.Host.dll"]
