FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5007
ENV ASPNETCORE_URLS=http://+:5007

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY . .

RUN dotnet restore "Ruoyu.Study.QuestionBank.sln"

RUN dotnet build "Ruoyu.Study.QuestionBank.sln" -c $BUILD_CONFIGURATION

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
WORKDIR "/src/src/Host"
RUN dotnet publish "Ruoyu.Study.QuestionBank.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

HEALTHCHECK --interval=30s --timeout=3s --start-period=60s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://localhost:5007/health || exit 1

ENTRYPOINT ["dotnet", "Ruoyu.Study.QuestionBank.Host.dll"]
