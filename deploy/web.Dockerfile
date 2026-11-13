# 建置 context 為 web/（套用 web/.dockerignore），nginx 設定以具名 context 帶入：
#   docker build -f deploy/web.Dockerfile --build-context deploy=deploy -t projectc/web web
# 基底映像 tag 釘具體版本，查證日期 2026-11-10（Docker Hub：library/node 的 22.22-alpine、
# nginxinc/nginx-unprivileged 的 1.30.6-alpine，後者為 stable 1.30 線），並以 @sha256 釘多架構索引的 digest
# （2026-11-14 取得；更新步驟見 docs/2026-11-13-image-pinning.md）。
FROM node:22.22-alpine@sha256:e58326d0d441090181ac150dc2078d3e2cf6a0d42e809aebba3ef5880935ffdd AS build
WORKDIR /app
# .npmrc 的 legacy-peer-deps=true 是 npm ci 解析 openapi-typescript（peer 仍為 TypeScript 5）所必需。
COPY package.json package-lock.json .npmrc ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.30.6-alpine@sha256:f5116ed4606f3eb015dd4a8e28cb9b351476dc0ad4bc6e9f93b19cec130cdf03
COPY --from=build /app/dist /usr/share/nginx/html
COPY --from=deploy nginx/default.conf /etc/nginx/conf.d/default.conf
COPY --from=deploy nginx/security-headers.conf /etc/nginx/snippets/security-headers.conf
EXPOSE 8080
