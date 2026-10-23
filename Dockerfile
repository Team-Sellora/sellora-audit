FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/Sellora.AuditService.Api/Sellora.AuditService.Api.csproj", "src/Sellora.AuditService.Api/"]
COPY ["src/Sellora.AuditService.Application/Sellora.AuditService.Application.csproj", "src/Sellora.AuditService.Application/"]
COPY ["src/Sellora.AuditService.Domain/Sellora.AuditService.Domain.csproj", "src/Sellora.AuditService.Domain/"]
COPY ["src/Sellora.AuditService.Infrastructure/Sellora.AuditService.Infrastructure.csproj", "src/Sellora.AuditService.Infrastructure/"]

RUN dotnet restore src/Sellora.AuditService.Api/Sellora.AuditService.Api.csproj

COPY src/ src/

RUN dotnet publish src/Sellora.AuditService.Api/Sellora.AuditService.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# US-E7-3-D2: QuestPDF's native renderer needs fontconfig on the slim ASP.NET image.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080

ARG BUILD_COMMIT_SHA=unknown
ENV BUILD_COMMIT_SHA=$BUILD_COMMIT_SHA
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Sellora.AuditService.Api.dll"]
