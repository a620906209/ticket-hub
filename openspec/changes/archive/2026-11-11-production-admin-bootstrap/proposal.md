## Why

Production 環境目前沒有任何建立首位 Admin 的機制：既有 Admin 只來自 `DevelopmentDataSeeder`（僅 `Development` 執行）與 migration 回填。K8s 清單因此被迫把 `ASPNETCORE_ENVIRONMENT` 設為 `Development` 去借用 seeder，連帶開啟 OpenAPI／SwaggerUI，無法安全帶到共享或對外環境。這是 `docs/todo/` 多份待辦（deployment-env-vars、deployment-prerequisites、k8s-todo）共同卡住的前置條件。

## What Changes

- 新增獨立的「Production Admin 啟動建立」機制：api 啟動時讀取 `AdminBootstrap__Email`、`AdminBootstrap__Password`（密碼來自 Secret），**僅在資料庫完全沒有任何 Admin 時**建立一位 Admin（不依 Email 判斷、不覆寫任何既有帳號、不建立 Organizer）。
- 設定規則：兩者皆未設定 → 略過並記 Info；只設定其一或值不合法（Email 格式、密碼強度同註冊規則）→ 啟動失敗；有未套用的 migration → 略過並記 Warning。密碼與雜湊值不得出現在日誌。
- 多副本並發啟動：以 `Members.Email` 唯一索引保證只會建立一筆，唯一性衝突視為「已存在」。
- 不修改 `DevelopmentDataSeeder`；Development 行為不變。
- K8s：`k8s/10-configmap.yaml` 改為 `Production` 並加入 `AdminBootstrap__Email`，`secret.example.yaml` 與 `verify-k8s-manifests.sh` 改用 `AdminBootstrap__Password`；更新 `docs/todo/2026-11-10-deployment-env-vars.md`。

## Capabilities

### New Capabilities
- `admin-bootstrap`: 啟動時於資料庫無任何 Admin 時，依設定建立首位 Admin 的行為與設定驗證規則。

### Modified Capabilities
（無——既有 `member-management`、`authentication` 的需求不變；新建立的 Admin 走既有登入流程。）

## Impact

- 程式：`Infrastructure/Persistence/Seeding/`（新 bootstrapper 與 options）、`WebApi/Extensions/`（DI 註冊、options 驗證）、`WebApi/Program.cs`（啟動呼叫）、`ServiceRegistrationSnapshot.baseline.txt`。
- 設定／部署：`k8s/10-configmap.yaml`、`k8s/examples/secret.example.yaml`、`scripts/verify-k8s-manifests.sh`、`docs/todo/2026-11-10-deployment-env-vars.md`。
- 排除：`DevelopmentDataSeeder`、Organizer 建立、K8s 真實瀏覽器 3.3 流程重跑（改走會員申請 + Admin 核准，文件註明）、雲端部署。
- 變更顆粒度：1 個 capability、預計 task 遠少於 30 項，不需拆分。
