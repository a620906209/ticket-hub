# 開發用容器：原始碼由 docker-compose 以 bind mount 掛載進來，此 image 只負責提供固定版本的
# Node 版本與工具鏈，不在 build 階段複製或安裝相依套件（node_modules 是 named volume，見 docker-compose.yml）。
FROM node:22-alpine

WORKDIR /app

# 每次啟動先確保相依套件是最新的（package.json 沒變動時 npm install 很快），再啟動 Vite dev server。
ENTRYPOINT ["sh", "-c", "npm install && npm run dev -- --host 0.0.0.0"]
