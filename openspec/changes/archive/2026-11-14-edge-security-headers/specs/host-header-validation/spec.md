# Spec Delta

## Purpose

定義 API 依設定的允許主機清單驗證請求的 Host 標頭，使部署環境能拒絕偽造 Host 的請求，同時保證探針與經邊界 nginx 轉發的合法請求不受影響。

## ADDED Requirements

### Requirement: 拒絕不在允許清單內的 Host
當 `AllowedHosts` 設定為明確主機清單時，API SHALL 對 `Host` 標頭不在清單內的請求回 400，不論路徑為何（含 `/health/live`、`/health/ready` 與 `/api/...`）；Host 在清單內（連接埠不影響比對）的請求 MUST 正常處理。

#### Scenario: HHV-001 不在清單內的 Host 被拒
- **WHEN** `AllowedHosts` 為 `app.example`，請求帶 `Host: evil.example`，路徑為 `/health/live` 或任一 `/api/...`
- **THEN** 回應狀態碼為 400（HostFiltering 於應用程式管線之前拒絕，本 change 的測試只斷言狀態碼）

#### Scenario: HHV-002 清單內的 Host 正常處理
- **WHEN** `AllowedHosts` 為 `app.example`，請求帶 `Host: app.example`（或 `app.example:8443`）請求 `/health/live`
- **THEN** 回應狀態碼為 200

#### Scenario: HHV-003 預設設定不限制 Host
- **WHEN** 使用專案內建預設設定（`AllowedHosts` 為 `*`），請求帶任意 `Host`
- **THEN** 請求正常處理，不因 Host 被拒

### Requirement: 部署設定使用明確主機清單
K8s 部署設定（`k8s/10-configmap.yaml`）MUST 以環境變數提供明確的 `AllowedHosts` 清單且 MUST NOT 為 `*`；清單 SHALL 包含該部署對外使用的主機名稱與叢集內服務名稱 `api`。部署前提文件 MUST 說明：上線前須將清單改為實際對外網域。

#### Scenario: HHV-004 K8s 設定不使用萬用字元
- **WHEN** 檢查 `k8s/10-configmap.yaml`
- **THEN** 存在 `AllowedHosts` 設定，值為非空且不含 `*` 的主機清單，含 `api`

#### Scenario: HHV-005 已部署的 API 拒絕偽造 Host
- **WHEN** 對已部署的 API 以 `Host: evil` 請求 `/health/live`（等同 `curl -H "Host: evil" <API 位址>/health/live`）
- **THEN** 回應狀態碼為 400

### Requirement: 探針與經 nginx 轉發的請求在限制 Host 下維持可用
K8s 的 startup、liveness、readiness 探針 SHALL 帶上清單內的 `Host` 標頭，使啟用 Host 限制後 Pod 仍能就緒；邊界 nginx 轉發至 API 的請求 MUST 保留原始 `Host`，因此使用者對外網域 MUST 在 `AllowedHosts` 清單內。

#### Scenario: HHV-006 啟用 Host 限制後 Pod 就緒
- **WHEN** 套用含明確 `AllowedHosts` 的 K8s 設定
- **THEN** API 的 Pod 通過 startup、liveness、readiness 探針並進入 Ready，滾動更新不因探針得 400 而失敗

#### Scenario: HHV-007 經 nginx 以合法網域存取
- **WHEN** 使用者以清單內的主機名稱經邊界 nginx 請求 `/api/...`
- **THEN** 請求由 API 正常處理，不因 Host 被拒

#### Scenario: HHV-008 經 nginx 以未列出的網域存取
- **WHEN** 使用者以不在清單內的主機名稱經邊界 nginx 請求 `/api/...`
- **THEN** 回應狀態碼為 400
