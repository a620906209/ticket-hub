# web — 售票平台前端

Vue 3（Composition API + `<script setup>`）+ TypeScript + Vite。跟後端一樣跑在 Docker Compose 裡，本機不需要安裝 Node.js。

## 啟動

在專案根目錄（`docker-compose.yml` 所在位置）執行：

```
docker compose up -d web
```

`web` service 會先在容器內執行 `npm install`（`node_modules` 是 named volume，不透過 bind mount），再啟動 Vite dev server。啟動後開啟 `http://localhost:${WEB_HOST_PORT:-5173}`。

原始碼以 bind mount 掛載，修改 `web/src` 底下的檔案會觸發 Vite HMR，不需要重啟容器。若修改了 `package.json`（新增套件），容器需要重啟一次讓 entrypoint 重新執行 `npm install`：

```
docker compose restart web
```

## 型別產生（`generate:api-types`）

`src/types/api.generated.ts` 是用 `openapi-typescript` 從後端的 OpenAPI 文件產生的，**禁止手動編輯**（下次重新產生會整檔覆蓋）。此檔案進版控，不是 build 產出物。

後端 Request DTO 有變動時，需要在 `web` 容器內重新產生（容器對容器連線，用 compose service name `api`，不是 `localhost`）：

```
docker compose up -d web api
docker compose exec web npm run generate:api-types
```

**已知限制**：`openapi-typescript` 只能反推出 Request 型別，後端 Controller 都用 `IActionResult` 回傳，Response 的 schema 反推不出來。Response DTO（`EventSummary`、`MemberProfile`、`OrderSummary` 等）改放在 `src/types/apiResponses.ts`，手寫維護——後端這幾個 DTO 改欄位時，這個檔案不會在編譯期自動抓到，需要開發者自行對照修改。

## 測試

```
docker compose exec web npm run test
```

目前只有 Vitest 單元測試（`src/api/`、`src/stores/`、`src/router/`），涵蓋 API client、auth store、路由守衛的邏輯正確性；沒有元件測試、沒有 E2E，畫面呈現與導頁行為要手動在瀏覽器驗證。

## 其他指令

```
docker compose exec web npm run lint    # ESLint
docker compose exec web npm run build   # production build 健檢（vue-tsc + vite build）
```

## 已知限制（本輪範圍）

- Admin 場館／座位圖沒有查詢 API：場館/座位圖列表只顯示當前瀏覽器分頁 session 內建立過的紀錄；建立活動時的場館 Id／座位圖 Id 要手動輸入（從場館管理頁複製）。
- 買家「我的訂單」列表/明細只有空狀態畫面，尚未串接查詢 API（後端還沒有買家專屬的訂單查詢端點）。

這兩項的完整脈絡見 `openspec/changes/ticketing-web-ui/design.md`。
