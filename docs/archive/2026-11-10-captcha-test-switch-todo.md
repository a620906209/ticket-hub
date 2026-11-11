# 驗證碼（captcha）測試用開關（2026-11-10）

## 背景

`k8s-local-manifests` 的 task 3.3（真實瀏覽器流程）因 `LoginHandler`、`RegisterMemberHandler`、`JoinPurchaseQueueHandler` 一律注入 `ICaptchaService` 並驗證，且 `Captcha` 設定區段只有 `TtlSeconds`，沒有任何可關閉的鍵，agent 無法自動登入與下單，只能由使用者手動填驗證碼（Admin 登入、一般會員登入、下單各一次）。

## 目標

在不削弱 Production 防護的前提下，讓 agent 與自動化腳本可以在 Development／驗證環境完成「登入、註冊、加入購買佇列」。

## 約束

- Production 絕不可被設定檔意外關閉：啟動驗證須拒絕 Production 環境下的關閉設定，或在 Production 直接忽略該鍵。
- 開關關閉時行為須明確（驗證一律通過，或改用固定答案），且需有測試涵蓋「Production 不可關閉」。
- 不使用「從 Redis 讀答案再填入」：等同繞過驗證碼，不採用。

## 待辦

- [x] 開新 change（需 spec，會影響 `captcha-verification` capability）：決定開關型式（`Captcha:Enabled` 或固定答案的 `ICaptchaService` 實作）與 Production 防護方式。
- [x] 介面歸類沿用既有規則（`ICaptchaService` 位於 `Application/Common/Interfaces`），新增設定鍵時同步更新 `docs/api-conventions.md` 與 `deployment-env-vars` 的分類。（design 判定無需更新 `docs/api-conventions.md`：該文件無設定鍵清單。）
- [x] 完成後，`k8s-local-manifests` 的 3.3 流程可改為全自動重跑，補驗 captcha 關閉時的完整購票流程。
  - 完成紀錄（2026-11-11，docker-desktop，master cf7e48d 重建 `projectc/api:local`，`KEEP_UP=1 bash scripts/verify-k8s-manifests.sh` → 14 passed, 0 failed, 1 unverified（8c））：對 projectc-verify 的 api 執行 `kubectl set env Captcha__VerificationMode=AcceptAny`；兩個新 Pod 啟動日誌各有一筆停用 Warning。claude-in-chrome 以錯誤驗證碼（`wrong`）登入 seeder Admin 成功（進入「我的主辦方」）、註冊 buyer2@local.dev 成功（DB `Members` 該列 Role=0）。限制：未重跑建立場館／活動／票種、下單與付款（下單與付款本無驗證碼，2026-11-10 已手動驗證）；驗證後已刪除 projectc-verify。
