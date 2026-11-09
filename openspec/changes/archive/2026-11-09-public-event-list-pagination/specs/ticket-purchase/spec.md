## MODIFIED Requirements

### Requirement: 公開活動列表附帶是否需實名
不需登入的公開活動列表查詢端點 SHALL 在每筆活動附帶 `IsRealNameRequired`，供前端顯示「本活動需實名」標示。此欄位可能來自 `query-caching` 能力的快取內容。由於此設定建立後不可變更，快取值與資料庫值不會因設定變更而不一致。

此列表的快取 key 依 `query-caching` 能力「活動列表快取 key 隨回應形狀版本化」使用 `query-cache:events:list:v3`，只存完整形狀的內容，不會讀到不含此欄位的快取項目。

#### Scenario: TP-BROWSE-RN-001 公開活動列表附帶是否需實名
- **WHEN** 使用者（不論是否登入）查詢公開活動列表，資料庫中有需實名與不需實名的活動
- **THEN** 每筆活動附帶與建立時一致的 `IsRealNameRequired`

