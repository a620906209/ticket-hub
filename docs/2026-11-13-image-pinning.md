# 映像 digest 釘版與更新步驟

`deploy/api.Dockerfile`、`deploy/web.Dockerfile` 的所有外部 `FROM`，以及 `k8s/` 的 PostgreSQL、Redis、Seq 映像，一律寫成 `name:tag@sha256:<digest>`：tag 保留供人閱讀，Docker 實際依 digest 取內容、忽略 tag，因此同一 tag 被重新發佈或覆寫時，建置與部署不會靜默拿到不同內容。本專案自建的 `projectc/*:local` 與 dev compose 的映像不在範圍內。

代價：基底映像的安全更新不再自動進來，須人工更新 digest。

## 取得 digest

取**多架構索引（manifest list／OCI index）**的 digest，否則在 Apple Silicon、CI 等不同架構會拉不到：

```bash
docker buildx imagetools inspect <name:tag>
```

輸出的 `Digest:` 一行即是索引 digest（`MediaType` 應為 `...manifest.list.v2+json` 或 `...image.index.v1+json`）。不要用 `docker inspect` 取得的 `RepoDigests` 或單一架構的 manifest digest，換架構時會拉不到。

## 更新

1. 決定新 tag（例如 `postgres:16-alpine` 的新版本、`dotnet/aspnet` 的新修補版）。
2. 以上面的指令取得該 tag 的 digest。
3. **tag 與 digest 一起改**：`image: postgres:16-alpine@sha256:<新 digest>`。只改 digest 不改 tag 會讓 tag 變成謊言；只改 tag 不改 digest 則實際內容不變。
4. `deploy/api.Dockerfile` 的 sdk、aspnet、runtime-deps 三個映像版本須一併檢查（見檔案開頭註解）。

## 更新後驗證

```bash
bash scripts/verify-image-pinning.sh     # 靜態檢查：所有外部映像皆含 @sha256:<64 位十六進位>，本文件關鍵字齊全
docker build -f deploy/api.Dockerfile --target runtime  -t projectc/api:local .
docker build -f deploy/api.Dockerfile --target migrator -t projectc/api-migrator:local .
docker build -f deploy/web.Dockerfile --build-context deploy=deploy -t projectc/web:local web
bash scripts/verify-k8s-manifests.sh     # 需要本機 docker-desktop 叢集；確認釘版後各工作負載仍能拉取並就緒
```
