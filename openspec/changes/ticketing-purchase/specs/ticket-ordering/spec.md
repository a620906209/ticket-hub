## ADDED Requirements

### Requirement: 建立訂單須記錄買家身份
系統 SHALL 在建立訂單時，將發起建立的已登入會員身份記錄為訂單的買家身份（`BuyerId`）；`BuyerId` 為訂單建立後不可變更的必填欄位，訂單 MUST NOT 在沒有買家身份的情況下建立。

#### Scenario: 建立訂單時記錄買家身份
- **WHEN** 已登入會員選定座位建立訂單
- **THEN** 訂單記錄的買家身份為該會員的 ID，且此後不可變更
