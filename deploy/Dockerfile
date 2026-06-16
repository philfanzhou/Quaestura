FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 5007
ENV ASPNETCORE_URLS=http://+:5007

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/services/ruoyu.questionBank/src/Contract/Ruoyu.Study.QuestionBank.Contract.csproj src/services/ruoyu.questionBank/src/Contract/
COPY src/services/ruoyu.questionBank/src/Database/Ruoyu.Study.QuestionBank.Database.csproj src/services/ruoyu.questionBank/src/Database/
COPY src/services/ruoyu.questionBank/src/Domain/Ruoyu.Study.QuestionBank.Domain.csproj src/services/ruoyu.questionBank/src/Domain/
COPY src/services/ruoyu.questionBank/src/Service/Ruoyu.Study.QuestionBank.Service.csproj src/services/ruoyu.questionBank/src/Service/
COPY src/services/ruoyu.questionBank/src/Host/Ruoyu.Study.QuestionBank.Host.csproj src/services/ruoyu.questionBank/src/Host/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj src/services/ruoyu.common/src/Ruoyu.Study.Common/

RUN dotnet restore "src/services/ruoyu.questionBank/src/Host/Ruoyu.Study.QuestionBank.Host.csproj"

COPY src/services/ruoyu.questionBank/src/ src/services/ruoyu.questionBank/src/
COPY src/services/ruoyu.common/src/ src/services/ruoyu.common/src/

RUN dotnet publish "src/services/ruoyu.questionBank/src/Host/Ruoyu.Study.QuestionBank.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Ruoyu.Study.QuestionBank.Host.dll"]
