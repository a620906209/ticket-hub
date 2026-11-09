## MODIFIED Requirements

### Requirement: 部署切換與 migration 回滾不得讓實名資料或實名活動設定靜默失效
系統 SHALL 確保部署切換期間的快取讀取，以及 migration 回滾，都不會讓已登記的實名資料或需實名活動的設定靜默失效；違反前置條件時 MUST 明確失敗。

- **部署切換期間的保證範圍**（系統行為）：
  - 下單、加入排隊、核銷三個後端判斷 MUST 以資料庫中的 `IsRealNameRequired` 為準，MUST NOT 讀取公開活動列表快取。所以已建立且設定為需實名的活動，不會因快取或部署切換而被這三個判斷視為不需實名。
  - 公開活動列表 API 的 `isRealNameRequired` 可能在快取 TTL 內落後資料庫（快取 key 已依 `query-caching`「活動列表快取 key 隨回應形狀版本化」改為只存完整形狀的 v3，不會讀到不含此欄位的項目）。這只影響列表與活動頁的標示。
  - 部署順序與「不以多實例滾動部署」屬於營運規範，列在 design.md Migration Plan 的部署檢查清單，不是本 Requirement 的驗收情境。
- **migration Down 的前置條件**：Down MUST 在執行時先檢查資料庫；有任何會員已登記實名，或有任何活動 `IsRealNameRequired = true` 時，Down MUST 失敗並中止，不刪除任何欄位或資料。只有兩者皆不存在時（此時刪除不會遺失任何實名資料，也不會讓任何已售票券失去實名要求），Down 才可執行。
- **必須回滾但已有資料時**：優先只回滾程式碼、保留 schema。新增欄位舊版不讀不寫，保留是安全的。若真的必須移除 schema，須以人工程序先備份 `Members.RealName`、`Members.NationalIdLast4`、`Events.IsRealNameRequired`，清除後再執行 Down；重新升版時以備份還原這三個欄位後，才可重新對外服務。此人工程序不在本次自動化範圍內。
- **回滾後重新升版**：Down 成功執行後再次套用 Up，所有會員皆為未登記、所有活動皆為不需實名。由於 Down 的前置條件保證執行前就沒有這些資料，這個結果與 Down 之前一致，沒有已售票券被錯誤視為非實名。

#### Scenario: RNV-ROLLBACK-001 已有會員登記實名時 Down 失敗
- **WHEN** 資料庫中有一位會員已登記實名，執行本次 migration 的 Down
- **THEN** Down 失敗並回報原因，`Members` 的兩個實名欄位、`Events.IsRealNameRequired` 欄位與其資料都仍存在

#### Scenario: RNV-ROLLBACK-002 已有需實名活動時 Down 失敗
- **WHEN** 資料庫中沒有任何會員登記實名，但有一個 `IsRealNameRequired = true` 的活動，執行本次 migration 的 Down
- **THEN** Down 失敗並回報原因，所有欄位與資料都仍存在

#### Scenario: RNV-ROLLBACK-003 沒有實名資料時 Down 成功，重新升版行為一致
- **WHEN** 資料庫中沒有任何會員登記實名，也沒有需實名活動，執行 Down 之後再執行 Up
- **THEN** Down 成功移除欄位與約束；Up 後所有會員為未登記、所有活動 `IsRealNameRequired = false`，與 Down 之前的資料狀態一致

#### Scenario: RNV-CACHE-001 列表快取顯示不需實名時，後端閘門仍以資料庫為準
- **WHEN** 資料庫中活動 `IsRealNameRequired = true`，但公開活動列表快取中該活動的項目為 `isRealNameRequired = false`（以測試直接寫入快取模擬），未登記實名的會員對該活動下單，另一次加入排隊（加入排隊的情境中活動已開啟熱門搶購模式）
- **THEN** 兩者都回傳 403、`Title = "RealNameRequired"`；公開活動列表此時仍回傳快取中的 false

