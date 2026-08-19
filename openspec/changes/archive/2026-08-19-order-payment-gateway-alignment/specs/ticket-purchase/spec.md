## MODIFIED Requirements

### Requirement: 透過 API 確認訂單（模擬付款）
系統 SHALL 提供已登入會員確認自己所屬 Pending 訂單的端點；此端點不接受任何付款資訊，改由系統呼叫 `IPaymentGateway` 完成付款，付款結果由伺服器端設定決定（呼叫端無法控制成功或失敗）。系統 MUST 先依既有 `ticket-ordering` 能力的確認驗證規則（訂單狀態、逾時、座位歸屬）完成驗證，驗證通過後才呼叫付款；付款成功才將訂單標記為已付款，付款失敗則訂單維持 Pending。非訂單買家本人呼叫 MUST 被拒絕。

#### Scenario: 買家確認自己的訂單成功
- **WHEN** 訂單的買家本人，對尚未逾時的 Pending 訂單呼叫確認端點，且付款成功
- **THEN** 訂單狀態轉為 Paid，訂單內所有座位轉為 Sold

#### Scenario: 付款失敗
- **WHEN** 訂單的買家本人，對尚未逾時的 Pending 訂單呼叫確認端點，但 `IPaymentGateway` 回報付款失敗
- **THEN** 系統 MUST 拒絕此次確認，訂單狀態維持 Pending，訂單內所有座位維持原本的持有狀態（不轉為 Sold），買家可在保留時間內重試

#### Scenario: 非本人確認他人訂單
- **WHEN** 非訂單買家的已登入會員呼叫確認端點
- **THEN** 系統 MUST 拒絕此次操作，回傳 403，不變更訂單或座位狀態，不呼叫付款

#### Scenario: 確認不存在的訂單
- **WHEN** 已登入會員對不存在的訂單 ID 呼叫確認端點
- **THEN** 系統回傳 404，不開啟交易也不鎖定任何座位，不呼叫付款
