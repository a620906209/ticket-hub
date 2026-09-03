## MODIFIED Requirements

### Requirement: 排隊入場名額依先後順序推進，且有名額上限
系統 SHALL 以週期性背景處理，針對每個 `IsQueueModeEnabled = true` 的活動，計算目前有效入場名額（**定義為狀態恰為 `Admitted` 且未逾時的紀錄數**——一筆紀錄只要離開 `Admitted` 狀態（轉為 `Completed` 或 `Expired`），即刻不再計入有效入場名額，不論轉出的原因或時間點）與設定的上限值；有剩餘名額時，依 `JoinedAtUtc ASC, Id ASC`（`Id` 作為加入時間相同時的排序依據，確保排序結果穩定、可重現）由舊到新，依序將 `Waiting` 紀錄推進為 `Admitted` 並設定入場逾時時間，直到補滿上限或無更多 `Waiting` 紀錄。同一活動的名額計算與推進 MUST 具備交易一致性，不得因併發處理而超額入場。已入場但超過入場逾時時間仍未完成訂單的紀錄，系統 SHALL 標記為 `Expired`，該名額自狀態轉換的當下起不再計入有效名額，供下一輪推進使用。

#### Scenario: PQ-ADMIT-001 有剩餘名額時推進等待中的排隊
- **WHEN** 某活動目前有效入場名額未達上限，且存在 `Waiting` 狀態的排隊紀錄
- **THEN** 系統依 `JoinedAtUtc ASC, Id ASC` 排序最前的 `Waiting` 紀錄開始，依序推進為 `Admitted`，直到補滿上限或無更多等待紀錄

#### Scenario: PQ-ADMIT-002 名額已滿時不推進
- **WHEN** 某活動目前有效入場名額已達上限
- **THEN** 系統不將任何 `Waiting` 紀錄推進為 `Admitted`，維持其等待狀態

#### Scenario: PQ-ADMIT-003 入場逾時釋放名額
- **WHEN** 某筆 `Admitted` 排隊紀錄已超過入場逾時時間，且該筆紀錄對應的訂單未完成建立
- **THEN** 系統將該紀錄標記為 `Expired`，釋放的名額於下一輪推進提供給依 `JoinedAtUtc ASC, Id ASC` 排序最前的 `Waiting` 紀錄

#### Scenario: PQ-ADMIT-004 併發推進不超額入場
- **WHEN** 背景處理同時間針對同一活動計算名額與推進排隊
- **THEN** 系統 MUST 確保同一活動同時只有一筆交易真正取得列鎖（`GetForUpdateAsync`）並寫入排隊紀錄，最終有效入場名額不超過設定上限。**範圍釐清（因 `purchase-queue-leader-election` 能力新增而補充，見該能力 spec.md PQLE-006a）**：本 Scenario 保證的是「資料庫交易層級」的序列化與最終正確性，不是「呼叫層級只會有一個推進呼叫在執行」。單一實例部署下兩者恆等；啟用 `purchase-queue-leader-election` 分散式鎖的多實例部署下，鎖租約（TTL）有效期間內兩者也恆等——但若原持有鎖的實例執行時間超過 TTL，另一實例可能取得新鎖並與前者的推進呼叫（方法呼叫層級）重疊執行，此時「呼叫層級只有一個推進在進行」不再成立，但本 Scenario 真正要保證的「最終有效入場名額不超過設定上限」不受影響——`GetForUpdateAsync` 的列鎖獨立於分散式鎖之外運作，持續確保任一時刻只有一個交易真正在寫入同一活動的排隊紀錄
