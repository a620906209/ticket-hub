## ADDED Requirements

### Requirement: 建立活動時可指定販售期間，建立後不可變更
建立活動的管理 API SHALL 接受兩個選填欄位：`SalesStartAtUtc`（開賣時間）與 `SalesEndAtUtc`（停售時間）。兩者隨活動建立一併儲存；系統 MUST NOT 提供任何在活動建立後變更它們的途徑。

- **未提供時的語意**：
  - `SalesStartAtUtc` 未提供（null）代表無開賣限制，建立後立即可售
  - `SalesEndAtUtc` 未提供（null）代表停售時間沿用活動開始時間 `StartAtUtc`
  - 兩者皆未提供時，建立行為與本次變更前完全一致
- **實際販售期間**為左閉右開區間 `[SalesStartAtUtc, SalesEndAtUtc ?? StartAtUtc)`，`SalesStartAtUtc` 為 null 時沒有下界
- **驗證**：違反時 MUST 回 400 驗證錯誤，不建立活動與任何 `EventSeat`
  - `SalesEndAtUtc` 有值時 MUST 不晚於 `StartAtUtc`
  - `SalesStartAtUtc` 有值時 MUST 早於實際停售時間（`SalesEndAtUtc ?? StartAtUtc`）
  - 兩者有值時 MUST 為 UTC 時間（請求時間字串帶 `Z`）
  - 系統 MUST NOT 要求開賣時間晚於現在：允許建立後立即開賣
- **列表**：後台專用的活動列表查詢端點 SHALL 在每筆活動附帶 `SalesStartAtUtc`、`SalesEndAtUtc` 的原始值（可為 null，不把 null 展開成其他值）
- **既有活動**：本次變更前已存在的活動，遷移後兩者皆為 null
- **回滾**：資料庫遷移的回滾（Down）在任何活動的兩欄位之一非 null 時 MUST 中止並報錯，不得靜默丟棄已設定的販售期間

#### Scenario: EVT-SALES-001 建立指定販售期間的活動
- **WHEN** 已切換至 Approved Organizer 的使用者建立活動，`StartAtUtc` 為 T+10 天，`SalesStartAtUtc` 為 T+1 天，`SalesEndAtUtc` 為 T+9 天
- **THEN** 系統成功建立活動，儲存的兩欄位與請求一致

#### Scenario: EVT-SALES-002 未提供販售期間時兩欄位為 null
- **WHEN** 已切換至 Approved Organizer 的使用者以本次變更前的既有格式建立活動，請求不含兩個新欄位
- **THEN** 系統成功建立活動，`SalesStartAtUtc` 與 `SalesEndAtUtc` 皆為 null

#### Scenario: EVT-SALES-003 只提供開賣時間
- **WHEN** 使用者建立活動，只提供早於 `StartAtUtc` 的 `SalesStartAtUtc`
- **THEN** 系統成功建立活動，`SalesEndAtUtc` 為 null

#### Scenario: EVT-SALES-004 停售時間晚於活動開始時間被拒
- **WHEN** 使用者建立活動，`SalesEndAtUtc` 晚於 `StartAtUtc`
- **THEN** 系統回傳 400，不建立活動與任何 `EventSeat`

#### Scenario: EVT-SALES-005 開賣時間不早於停售時間被拒
- **WHEN** 使用者建立活動，`SalesStartAtUtc` 等於或晚於 `SalesEndAtUtc`
- **THEN** 系統回傳 400，不建立活動

#### Scenario: EVT-SALES-006 未提供停售時間時，開賣時間不早於活動開始時間被拒
- **WHEN** 使用者建立活動，未提供 `SalesEndAtUtc`，`SalesStartAtUtc` 等於或晚於 `StartAtUtc`
- **THEN** 系統回傳 400，不建立活動

#### Scenario: EVT-SALES-007 開賣時間在過去仍可建立
- **WHEN** 使用者建立活動，`SalesStartAtUtc` 為現在之前的時間，且早於 `StartAtUtc`
- **THEN** 系統成功建立活動

#### Scenario: EVT-SALES-008 非 UTC 時間被拒
- **WHEN** 使用者建立活動，`SalesStartAtUtc` 為不帶 `Z` 的時間字串（例如 `2026-11-01T12:00:00`）
- **THEN** 系統回傳 400，不建立活動，不以 500 失敗

#### Scenario: EVT-SALES-009 後台活動列表附帶販售期間原始值
- **WHEN** 已切換至 Organizer A 的使用者查詢後台活動列表，名下同時有設定販售期間與未設定的活動
- **THEN** 每筆活動附帶與建立時一致的 `SalesStartAtUtc`、`SalesEndAtUtc`，未設定者為 null

#### Scenario: EVT-SALES-010 既有活動遷移後兩欄位為 null
- **WHEN** 套用本次資料庫遷移前已存在的活動，在遷移後被查詢
- **THEN** 該活動的 `SalesStartAtUtc` 與 `SalesEndAtUtc` 皆為 null

#### Scenario: EVT-SALES-011 有活動設定販售期間時回滾遷移被中止
- **WHEN** 資料庫中有活動的 `SalesStartAtUtc` 非 null，執行本次遷移的 Down
- **THEN** 遷移以錯誤中止，`Events` 的兩欄位與資料保持不變

#### Scenario: EVT-SALES-012 沒有活動設定販售期間時可回滾遷移
- **WHEN** 資料庫中所有活動的兩欄位皆為 null，執行本次遷移的 Down
- **THEN** 遷移成功，兩欄位被移除
