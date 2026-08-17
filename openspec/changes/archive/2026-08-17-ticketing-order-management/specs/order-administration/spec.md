## ADDED Requirements

### Requirement: 查看訂單需要 Admin 角色
系統 SHALL 要求呼叫訂單列表、訂單明細端點者持有效 JWT 且角色為 Admin；未提供有效 Token 或角色非 Admin MUST 被拒絕。

#### Scenario: Admin 成功查詢訂單
- **WHEN** 持有效 JWT 且角色為 Admin 的使用者呼叫訂單列表或訂單明細端點
- **THEN** 系統受理該請求並依端點邏輯處理

#### Scenario: 非 Admin 會員查詢訂單
- **WHEN** 持有效 JWT 但角色非 Admin 的使用者呼叫訂單列表或訂單明細端點
- **THEN** 系統回傳 403 拒絕存取

#### Scenario: 未帶 Token 查詢訂單
- **WHEN** 未提供 Authorization Header 或 Token 無效，呼叫訂單列表或訂單明細端點
- **THEN** 系統回傳 401 未授權

### Requirement: 查詢所有訂單列表
系統 SHALL 提供 Admin 查詢目前所有訂單的端點，回傳每筆訂單的即時狀態（依 `GetStatus(now)` 推導，可能為 Expired）。

#### Scenario: 查詢訂單列表
- **WHEN** Admin 查詢訂單列表
- **THEN** 系統回傳目前所有訂單的基本資訊與即時狀態

### Requirement: 查詢單筆訂單明細
系統 SHALL 提供 Admin 依訂單 ID 查詢單筆訂單明細的端點，回傳訂單內每筆座位項目；訂單不存在 MUST 回報找不到資源。

#### Scenario: 查詢存在的訂單明細
- **WHEN** Admin 以存在的訂單 ID 查詢訂單明細
- **THEN** 系統回傳該訂單的即時狀態與內部每筆座位項目

#### Scenario: 查詢不存在的訂單
- **WHEN** Admin 以不存在的訂單 ID 查詢訂單明細
- **THEN** 系統回傳 404 找不到資源

### Requirement: 背景週期性清理逾時仍為 Pending 的訂單
系統 SHALL 以固定週期背景執行清理程序，找出狀態為 Pending 且已超過到期時間（`HeldUntilUtc`）的訂單，依既有 `ticket-ordering` 能力的取消規則將其轉為 Cancelled，並釋放訂單內仍由該訂單持有的座位；此清理不需要、也不驗證任何買家身份，因為是系統依訂單自身逾時狀態主動觸發，不是任何買家發起的請求。單筆訂單處理失敗（無論是正常的業務規則拒絕，或是可回復的基礎設施例外）MUST NOT 中斷其餘訂單的清理；應用程式關閉（取消）訊號不算「單筆失敗」，MUST 讓清理程序正常停止，不得被當成失敗吞掉後繼續處理下一筆。

#### Scenario: 逾時的 Pending 訂單被背景清理
- **WHEN** 背景清理程序執行，且資料庫中存在一筆狀態為 Pending、已超過到期時間的訂單
- **THEN** 該訂單狀態轉為 Cancelled，訂單內仍由該訂單持有的座位釋放回 Available

#### Scenario: 尚未逾時的 Pending 訂單不受影響
- **WHEN** 背景清理程序執行，且資料庫中存在一筆狀態為 Pending、尚未超過到期時間的訂單
- **THEN** 該訂單狀態與座位鎖定維持不變，不被清理程序處理

#### Scenario: 已是終態的訂單不受影響
- **WHEN** 背景清理程序執行，且資料庫中存在狀態為 Confirmed 或 Cancelled 的訂單
- **THEN** 這些訂單不被清理程序掃描或處理

#### Scenario: 單筆訂單清理失敗不影響其餘訂單
- **WHEN** 背景清理程序處理多筆逾時訂單，其中一筆處理時被業務規則拒絕（回傳失敗結果，而非拋出例外）
- **THEN** 系統繼續處理其餘逾時訂單，不因單一筆失敗而整批中斷

#### Scenario: 應用程式關閉時清理程序正常停止，不當成單筆失敗處理
- **WHEN** 背景清理程序收到應用程式關閉訊號
- **THEN** 系統停止目前的清理流程、不開始處理後續訂單，且不將這次中斷記錄成某一筆訂單的清理失敗
