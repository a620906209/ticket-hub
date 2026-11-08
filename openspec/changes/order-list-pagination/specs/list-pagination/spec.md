# Spec Delta

## Purpose

定義列表端點共用的 offset 分頁慣例：請求參數、驗證、回應結構與 `pageSize` 上限，避免列表端點被當成整表匯出，並讓各列表端點行為一致。

## ADDED Requirements

### Requirement: 分頁請求參數與預設值
採用分頁的列表端點 SHALL 接受查詢參數 `page`（從 1 起算的頁碼）與 `pageSize`（每頁筆數）。未提供 `page` 時 SHALL 視為 1，未提供 `pageSize` 時 SHALL 視為 20。端點 MUST NOT 提供任何繞過分頁、一次取得全部資料的參數。

#### Scenario: LP-PARAM-001 未帶分頁參數使用預設值
- **WHEN** 呼叫端呼叫採用分頁的列表端點，未帶 `page` 與 `pageSize`
- **THEN** 回應的 `page` 為 1、`pageSize` 為 20，`items` 至多 20 筆

#### Scenario: LP-PARAM-002 只帶其中一個參數時另一個使用預設值
- **WHEN** 呼叫端只帶 `pageSize=5`
- **THEN** 回應的 `page` 為 1、`pageSize` 為 5

### Requirement: 分頁參數越界回 400
`page` 小於 1 或大於 10000、`pageSize` 小於 1 或大於 100 時，系統 SHALL 回應 400 `Validation` 錯誤，並以逐欄位 `errors` 指出 `page` 或 `pageSize`（格式見 `api-error-responses` 能力），MUST NOT 靜默截斷為上限或改用預設值。`page` 或 `pageSize` 不是整數（含超出 32 位元整數範圍的值）時，系統 SHALL 同樣回應 400 `Validation` 錯誤。

#### Scenario: LP-VALID-001 pageSize 超過上限
- **WHEN** 呼叫端帶 `pageSize=101`
- **THEN** 系統回應 400，`title` 為 `Validation`，`errors` 含 `pageSize` 欄位，不回傳任何資料

#### Scenario: LP-VALID-002 pageSize 等於上限
- **WHEN** 呼叫端帶 `pageSize=100`
- **THEN** 系統正常回應，`pageSize` 為 100

#### Scenario: LP-VALID-003 page 與 pageSize 小於 1
- **WHEN** 呼叫端分別帶 `page=0`、`page=-1`、`pageSize=0`
- **THEN** 每次皆回應 400，`errors` 分別指出 `page`、`page`、`pageSize`

#### Scenario: LP-VALID-006 page 超過上限
- **WHEN** 呼叫端帶 `page=10001`，或帶 `page=2147483647`、`pageSize=100`
- **THEN** 系統回應 400 `Validation` 錯誤，`errors` 含 `page` 欄位，不發生數值溢位或 500

#### Scenario: LP-VALID-007 page 等於上限
- **WHEN** 呼叫端帶 `page=10000`、`pageSize=100`
- **THEN** 系統正常回應 200，資料不足時 `items` 為空陣列

#### Scenario: LP-VALID-004 非整數參數
- **WHEN** 呼叫端帶 `page=abc`
- **THEN** 系統回應 400 `Validation` 錯誤，`errors` 含 `page` 欄位

#### Scenario: LP-VALID-005 兩個參數同時越界
- **WHEN** 呼叫端帶 `page=0` 與 `pageSize=101`
- **THEN** 系統回應 400，`errors` 同時含 `page` 與 `pageSize` 欄位

### Requirement: 分頁回應結構
採用分頁的列表端點 SHALL 回傳物件 `{ items, page, pageSize, totalCount }`：`items` 為該頁資料陣列，`page` 與 `pageSize` 為實際套用的值，`totalCount` 為符合條件（含呼叫端的授權範圍）的資料總筆數，不受分頁影響。`page` 超過最後一頁時 SHALL 回應 200，`items` 為空陣列、`totalCount` 仍為實際總筆數，不視為錯誤。沒有任何資料時 `items` 為空陣列、`totalCount` 為 0。

#### Scenario: LP-RESP-001 回應含分頁欄位與總筆數
- **WHEN** 呼叫端的授權範圍內共有 45 筆資料，以 `page=2`、`pageSize=20` 呼叫
- **THEN** 回應的 `items` 為第 21–40 筆、`page` 為 2、`pageSize` 為 20、`totalCount` 為 45

#### Scenario: LP-RESP-002 最後一頁不足一頁
- **WHEN** 同上資料，以 `page=3`、`pageSize=20` 呼叫
- **THEN** `items` 為第 41–45 筆共 5 筆，`totalCount` 為 45

#### Scenario: LP-RESP-003 頁碼超過最後一頁
- **WHEN** 同上資料，以 `page=4`、`pageSize=20` 呼叫
- **THEN** 系統回應 200，`items` 為空陣列，`page` 為 4，`totalCount` 為 45

#### Scenario: LP-RESP-004 沒有任何資料
- **WHEN** 呼叫端的授權範圍內沒有任何資料
- **THEN** 系統回應 200，`items` 為空陣列，`totalCount` 為 0

### Requirement: 分頁排序必須確定
採用分頁的列表端點 SHALL 使用確定性排序（含唯一的次要鍵），使同一份資料以相同 `pageSize` 逐頁讀取時，每筆資料恰好出現一次。端點 MUST NOT 允許呼叫端自行指定排序欄位。

#### Scenario: LP-ORDER-001 逐頁讀取不重複不遺漏
- **WHEN** 呼叫端以相同 `pageSize` 由第 1 頁讀到最後一頁，期間資料沒有變動
- **THEN** 所有頁的 `items` 合併後，每筆資料恰好出現一次
