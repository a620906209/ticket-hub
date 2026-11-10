# production 映像（不含 SDK、不靠 bind mount）。建置 context 為專案根目錄：
#   docker build -f deploy/api.Dockerfile --target runtime  -t projectc/api .
#   docker build -f deploy/api.Dockerfile --target migrator -t projectc/api-migrator .
# 基底映像 tag 釘具體版本，查證日期 2026-11-10（mcr.microsoft.com 的 tag 清單 dotnet/sdk、aspnet、runtime-deps）；
# 未釘 digest（design.md Risks）。更新時三個映像的版本請一併檢查。

FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /src

# 先只複製專案檔與中央套件管理檔做 restore，讓相依層可被快取。
COPY Directory.Packages.props ./
COPY src/ProjectC.Domain/ProjectC.Domain.csproj src/ProjectC.Domain/
COPY src/ProjectC.Application/ProjectC.Application.csproj src/ProjectC.Application/
COPY src/ProjectC.Infrastructure/ProjectC.Infrastructure.csproj src/ProjectC.Infrastructure/
COPY src/ProjectC.WebApi/ProjectC.WebApi.csproj src/ProjectC.WebApi/
RUN dotnet restore src/ProjectC.WebApi/ProjectC.WebApi.csproj

COPY src/ src/
RUN dotnet publish src/ProjectC.WebApi/ProjectC.WebApi.csproj -c Release -o /app/publish --no-restore --no-self-contained

# EF migration bundle：自包含單一執行檔，runtime 映像不需要 SDK 或 dotnet-ef。
FROM build AS bundle
RUN dotnet tool install --global dotnet-ef --version "10.*"
ENV PATH="${PATH}:/root/.dotnet/tools"
RUN dotnet ef migrations bundle \
    --project src/ProjectC.Infrastructure \
    --startup-project src/ProjectC.WebApi \
    --configuration Release \
    --self-contained -r linux-x64 \
    -o /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# APP_UID 為基底映像內建的非 root 使用者（數字 UID），日後 K8s runAsNonRoot 可直接驗證。
# 不設 HEALTHCHECK：aspnet 映像沒有 curl，探針交給編排層（/health/live、/health/ready）。
USER $APP_UID
ENTRYPOINT ["dotnet", "ProjectC.WebApi.dll"]

# 用法：migrator --connection "<連線字串>"。密碼會出現在命令列；K8s Job 日後應改由 Secret 以環境變數注入。
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14 AS migrator
WORKDIR /app
COPY --from=bundle /out/efbundle .
# 自包含單一檔案若需解壓原生函式庫，指向非 root 可寫路徑，不改回 root。
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
USER $APP_UID
ENTRYPOINT ["./efbundle"]
