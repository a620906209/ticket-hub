# 開發用容器：原始碼由 docker-compose 以 bind mount 掛載進來，此 image 只負責提供固定版本的 .NET SDK
# 與工具鏈（dotnet watch / dotnet ef），不在 build 階段複製或還原原始碼。
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH="${PATH}:/root/.dotnet/tools"

RUN dotnet tool install --global dotnet-ef

WORKDIR /src

# 監看 WebApi 專案並支援 hot reload；連線字串等設定由 docker-compose 的 env_file 注入。
ENTRYPOINT ["dotnet", "watch", "--project", "src/ProjectC.WebApi/ProjectC.WebApi.csproj", "run", "--no-launch-profile", "--urls", "http://0.0.0.0:8080"]
