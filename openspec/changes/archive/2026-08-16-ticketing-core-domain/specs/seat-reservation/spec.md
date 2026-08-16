## ADDED Requirements

### Requirement: 座位庫存狀態機（EventSeat）
每筆活動專屬座位庫存（`EventSeat`）SHALL 具有狀態 Available（可售）、Held（暫扣）、Sold（已售出）三種狀態，狀態轉換 MUST 只能依 Available → Held → Sold，或 Held → Available（釋放／逾時後被覆寫）進行；Sold 狀態一經標記 MUST 為永久狀態，不受時間經過影響，也不允許再被鎖定或釋放。

#### Scenario: 鎖定可售座位
- **WHEN** 對狀態為 Available 的 `EventSeat` 執行鎖定，指定訂單編號與暫扣逾時時間
- **THEN** 座位狀態轉為 Held，並記錄鎖定訂單編號與逾時時間

#### Scenario: 鎖定已被有效鎖定的座位
- **WHEN** 對狀態為 Held 且尚未逾時的 `EventSeat` 再次執行鎖定
- **THEN** 系統 MUST 拒絕此次鎖定並拋出座位已被鎖定的領域例外，座位狀態維持不變

#### Scenario: 鎖定已售出的座位
- **WHEN** 對狀態為 Sold 的 `EventSeat` 執行鎖定
- **THEN** 系統 MUST 拒絕此次鎖定並拋出座位不可鎖定的領域例外

### Requirement: 座位狀態存取限制
系統 SHALL 只透過 `GetStatus(now)`、`IsAvailableForHold(now)`、`IsHeldBy(orderId, now)`、`IsSoldBy(orderId)` 四個方法對外暴露 `EventSeat` 的狀態判斷；`Sold` 由明確標記欄位（一旦設定即永久生效）判斷，`Held`／`Available` 才由暫扣欄位搭配傳入的 `now` 推導。`IsSoldBy(orderId)` 不需要 `now`，因為 Sold 不受時間影響。呼叫端（包含 Application 層需要確認「座位是否仍由某筆訂單合法持有」或「座位是否已由某筆訂單售出」的邏輯）MUST NOT 直接讀取內部欄位（例如 `HeldByOrderId`、`HeldUntilUtc`、`SoldByOrderId`）自行判斷，一律改呼叫上述方法。

#### Scenario: 已售出座位不受時間影響
- **WHEN** 對已標記 Sold 的 `EventSeat`，以任意時間點呼叫 `GetStatus(now)`
- **THEN** 系統一律回報狀態為 Sold，不因時間經過而改變

#### Scenario: 透過狀態方法取得目前可售性
- **WHEN** 呼叫 `IsAvailableForHold(now)` 判斷一筆 `EventSeat` 是否可被鎖定
- **THEN** 系統回傳的結果 MUST 與同一時間點呼叫 `GetStatus(now)` 是否為 Available 一致

#### Scenario: 透過 IsHeldBy 確認座位仍由指定訂單合法持有
- **WHEN** 座位處於 Held 狀態、暫扣尚未逾時、且鎖定訂單編號為訂單 A，呼叫 `IsHeldBy(訂單 A 編號, now)`
- **THEN** 系統回傳 true

#### Scenario: 透過 IsSoldBy 確認座位是否由指定訂單售出
- **WHEN** 座位已標記 Sold 且售出訂單編號為訂單 A，分別呼叫 `IsSoldBy(訂單 A 編號)` 與 `IsSoldBy(訂單 B 編號)`
- **THEN** 前者回傳 true，後者回傳 false；座位未售出時，對任何訂單編號呼叫 `IsSoldBy` 皆回傳 false

#### Scenario: IsHeldBy 對不符合的情況一律回傳 false
- **WHEN** 座位已售出、或暫扣已逾時、或鎖定訂單編號並非傳入的訂單編號，呼叫 `IsHeldBy(該訂單編號, now)`
- **THEN** 系統回傳 false，不拋出例外

### Requirement: 座位暫扣逾時後可被覆寫鎖定
系統 SHALL 以指定時間點判斷座位暫扣是否已逾時；當目前時間超過 `EventSeat` 記錄的暫扣逾時時間且尚未被標記 Sold，該座位 MUST 被視為已逾時，等同 Available，可再次被鎖定。鎖定已逾時座位時，MUST 直接覆寫原本的鎖定訂單編號與逾時時間為新的鎖定資訊，無論原本的鎖定屬於哪一筆訂單；此覆寫是呼叫端主動觸發鎖定操作所產生的正常寫入，不屬於系統背景/被動的寫回行為。

#### Scenario: 目前時間尚未超過暫扣逾時時間
- **WHEN** 以早於座位暫扣逾時時間的時間點檢查座位是否逾時
- **THEN** 系統回報尚未逾時，該座位仍視為 Held，不可被其他訂單鎖定

#### Scenario: 目前時間已超過暫扣逾時時間並被新訂單鎖定
- **WHEN** 以晚於座位暫扣逾時時間的時間點，對該座位執行鎖定並指定新的訂單編號與逾時時間
- **THEN** 系統允許此次鎖定，座位狀態轉為 Held，鎖定訂單編號與逾時時間被覆寫為新訂單的資料，原訂單編號不再與此座位關聯

### Requirement: 座位售出確認
系統 SHALL 只允許將座位由持有該座位有效暫扣的訂單標記為 Sold；非鎖定該座位的訂單或已逾時的暫扣 MUST 被拒絕。標記為 Sold 後 MUST 清除暫扣相關欄位，並記錄售出所屬的訂單編號。

#### Scenario: 持有有效暫扣的訂單確認售出
- **WHEN** 座位處於 Held 狀態且暫扣尚未逾時，由鎖定該座位的訂單執行售出確認
- **THEN** 座位狀態轉為 Sold，並記錄售出所屬訂單編號

#### Scenario: 非鎖定訂單嘗試確認售出
- **WHEN** 座位處於 Held 狀態，由非鎖定該座位的訂單編號執行售出確認
- **THEN** 系統 MUST 拒絕此次操作並拋出領域例外，座位狀態維持 Held

#### Scenario: 暫扣已逾時仍嘗試確認售出
- **WHEN** 座位的暫扣已逾時，原鎖定訂單嘗試執行售出確認
- **THEN** 系統 MUST 拒絕此次操作，座位須先視為已逾時（等同 Available）

### Requirement: 座位釋放限定持有訂單
系統 SHALL 提供以訂單編號限定的座位釋放操作（`ReleaseHold(orderId)`）：只有當座位目前確實由該訂單編號持有暫扣時，才會清除鎖定資訊並轉為 Available；若座位已標記 Sold，MUST 拒絕並拋出領域例外；若座位目前並非由該訂單編號持有（包含座位本來就是 Available，或已被其他訂單在逾時後重新鎖定），MUST 視為無操作，不改變座位任何欄位，也不拋出例外。

#### Scenario: 持有訂單釋放自己持有的暫扣座位
- **WHEN** 對狀態為 Held、且由訂單 A 持有的 `EventSeat`，以訂單 A 的編號執行 `ReleaseHold`
- **THEN** 座位狀態轉為 Available，鎖定訂單編號與暫扣逾時時間被清除

#### Scenario: 嘗試釋放已售出的座位
- **WHEN** 對狀態為 Sold 的 `EventSeat` 執行 `ReleaseHold`
- **THEN** 系統 MUST 拒絕此次操作並拋出領域例外，座位狀態維持 Sold

#### Scenario: 以非持有訂單的編號嘗試釋放
- **WHEN** 座位目前由訂單 B 持有暫扣（可能是逾時後被重新鎖定），以訂單 A 的編號執行 `ReleaseHold`
- **THEN** 系統不進行任何變更，座位狀態與鎖定資訊維持訂單 B 持有的現狀，不拋出例外
