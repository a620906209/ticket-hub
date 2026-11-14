# container-image-pinning Specification

## Purpose
要求生產路徑上的容器映像以內容 digest 釘版，使同一 tag 被重新發佈或覆寫時，建置與部署不會靜默取得不同內容，並提供可驗證的更新方式。

## Requirements

### Requirement: Dockerfile 的外部基底映像以 digest 釘版
`deploy/api.Dockerfile` 與 `deploy/web.Dockerfile` 中每一個引用外部映像的 `FROM`（不含引用前一階段名稱者）SHALL 寫成 `<name>:<tag>@sha256:<digest>`，保留 tag 供人閱讀；digest MUST 為多架構索引（manifest list）的 digest。

#### Scenario: CIP-001 所有外部 FROM 皆含 digest
- **WHEN** 檢查兩份 Dockerfile 的所有 `FROM`
- **THEN** 引用外部映像的 `FROM` 皆符合 `<name>:<tag>@sha256:<64 位十六進位>`，引用階段名稱（如 `FROM build`）不受此限

#### Scenario: CIP-002 釘版後映像可建置
- **WHEN** 以專案文件記載的指令建置 `projectc/api`、`projectc/api-migrator` 與 `projectc/web`
- **THEN** 三個映像皆建置成功

### Requirement: K8s 第三方映像以 digest 釘版
`k8s/` 內引用外部第三方映像（PostgreSQL、Redis、Seq）的 `image:` SHALL 寫成 `<name>:<tag>@sha256:<digest>`；本專案自建的 `projectc/*:local` 映像不受此限。

#### Scenario: CIP-003 第三方映像含 digest
- **WHEN** 檢查 `k8s/` 下所有 manifest 的 `image:` 欄位
- **THEN** 非 `projectc/` 開頭的映像皆含 `@sha256:<64 位十六進位>`

#### Scenario: CIP-004 釘版後叢集可就緒
- **WHEN** 套用釘版後的 `k8s/` manifest
- **THEN** 資料庫、Redis、Seq 與其餘工作負載皆能拉取映像並進入 Ready

### Requirement: 提供 digest 取得與更新步驟
專案文件 SHALL 說明如何取得某個 tag 對應的多架構 digest、更新時須同時修改 tag 與 digest，以及更新後須執行的驗證。

#### Scenario: CIP-005 文件涵蓋更新步驟
- **WHEN** 閱讀映像釘版的專案文件
- **THEN** 文件包含取得 digest 的指令、tag 與 digest 同步更新的要求，以及更新後的驗證指令
