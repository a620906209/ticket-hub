# admin-web-ui Specification

## Purpose
TBD - created by archiving change ticketing-web-ui. Update Purpose after archive.

## Requirements

### Requirement: Admin 後台路由僅限 Admin 角色進入
系統 SHALL 在使用者導覽至任何 `/admin/*` 路由時檢查目前登入角色；非 Admin（含未登入）SHALL 被導向登入頁或買家端首頁，不得進入後台頁面內容。

#### Scenario: 未登入使用者直接進入後台路由
- **WHEN** 未登入的使用者直接開啟任一 `/admin/*` 網址
- **THEN** 系統導向登入頁，不顯示後台頁面內容

#### Scenario: 一般會員嘗試進入後台路由
- **WHEN** 已登入但角色為一般會員的使用者開啟任一 `/admin/*` 網址
- **THEN** 系統導向買家端首頁，不顯示後台頁面內容

#### Scenario: Admin 登入後可進入後台
- **WHEN** 角色為 Admin 的使用者登入成功
- **THEN** 系統導向 Admin 後台首頁

### Requirement: Admin 可透過介面管理場館與座位圖
系統 SHALL 提供場館列表頁與建立場館／座位圖的表單，呼叫既有 `event-management` API 完成建立。後端目前僅提供建立用的 `POST` API，沒有查詢用的 `GET` API（見 design.md Non-Goals），故場館列表 SHALL 僅顯示當前瀏覽器分頁 session 內建立過的場館／座位圖，不透過查詢 API 取得，重新整理頁面後清單 SHALL 清空。建立座位圖表單 SHALL 同時支援「手動新增單一座位」與「批次產生」兩種輸入方式——批次產生以分區代碼＋起始號碼＋結束號碼一次展開成整批座位，供大量連號座位使用；兩種方式產生的座位在同一次建立座位圖時 SHALL 可以合併送出。

#### Scenario: 建立場館
- **WHEN** Admin 在建立場館表單填寫有效名稱並送出
- **THEN** 系統呼叫建立場館 API 成功，畫面顯示新場館的 Id（供複製）並加入本次 session 的暫存清單

#### Scenario: 手動新增少量座位並建立座位圖
- **WHEN** Admin 在某場館下用「手動新增」逐一輸入少量座位並送出
- **THEN** 系統呼叫建立座位圖 API 成功，畫面顯示新座位圖的 Id（供複製）

#### Scenario: 批次產生大量座位並建立座位圖
- **WHEN** Admin 在「批次產生」輸入分區代碼、起始號碼、結束號碼並加入這批，重複數次後送出建立座位圖
- **THEN** 系統把所有批次展開成個別座位物件，一次呼叫建立座位圖 API，成功後畫面顯示新座位圖的 Id 與座位總數

### Requirement: Admin 可透過介面管理活動與票種
系統 SHALL 提供活動列表頁與建立活動／票種的表單，呼叫既有 `event-management` API 完成建立；活動列表頁 SHALL 重用既有公開的 `GET /api/events` API。由於場館／座位圖沒有查詢 API，建立活動表單的場館 Id、座位圖 Id SHALL 為手動輸入欄位（前端驗證 GUID 格式），不提供下拉選單；活動列表顯示的場館／座位圖欄位 SHALL 顯示原始 Id，不查詢對應名稱。建立活動表單 SHALL 提供「活動說明」（多行文字）、「海報網址」（圖片連結）、「每筆訂單限購張數」（正整數）三個選填欄位；「活動說明」「海報網址」供買家端活動詳情頁顯示，「每筆訂單限購張數」供買家端選位時限制單筆訂單最多可選的座位數，留空代表不限制。這三個欄位都不填也 SHALL 能成功建立活動；若「每筆訂單限購張數」有填寫，SHALL 為正整數，否則系統 SHALL 顯示驗證錯誤、不呼叫建立活動 API。

#### Scenario: 建立活動
- **WHEN** Admin 在建立活動表單手動輸入有效格式的場館 Id、座位圖 Id 並填寫活動資訊送出
- **THEN** 系統呼叫建立活動 API 成功，活動列表顯示新活動

#### Scenario: 建立活動時填寫說明、海報網址與限購張數
- **WHEN** Admin 在建立活動表單額外填寫活動說明、海報網址、每筆訂單限購張數並送出
- **THEN** 系統呼叫建立活動 API 成功，這三個欄位隨活動資料一併儲存，買家端活動詳情頁能顯示說明/海報，選位時也會套用限購張數

#### Scenario: 建立活動時不填說明、海報網址與限購張數
- **WHEN** Admin 在建立活動表單留空活動說明、海報網址、每筆訂單限購張數並送出
- **THEN** 系統呼叫建立活動 API 成功，不因為這三個選填欄位空白而驗證失敗，買家端不限制選位張數

#### Scenario: 限購張數填寫非正整數
- **WHEN** Admin 在「每筆訂單限購張數」填寫 0 或負數
- **THEN** 系統顯示驗證錯誤訊息，不呼叫建立活動 API

#### Scenario: 輸入不存在的場館或座位圖 Id
- **WHEN** Admin 在建立活動表單輸入格式正確但不存在的場館 Id 或座位圖 Id 並送出
- **THEN** 系統呼叫建立活動 API 失敗，顯示後端回傳的錯誤訊息，不導向活動列表

#### Scenario: 為活動建立票種
- **WHEN** Admin 在某活動下建立票種並設定票價送出
- **THEN** 系統呼叫建立票種 API 成功

### Requirement: Admin 可查看所有訂單列表與明細
系統 SHALL 提供訂單列表頁與訂單詳情頁，呼叫既有 `order-administration` API 顯示所有訂單狀態與單筆訂單內的座位項目明細。此狀態為頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新。

#### Scenario: 查看所有訂單列表
- **WHEN** Admin 開啟後台訂單列表頁
- **THEN** 系統顯示目前所有訂單與其狀態

#### Scenario: 查看訂單明細
- **WHEN** Admin 點選某筆訂單進入詳情頁
- **THEN** 系統顯示該訂單內的每一筆座位項目明細
