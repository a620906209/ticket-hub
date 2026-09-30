# admin-web-ui Specification

## Purpose
TBD - created by archiving change ticketing-web-ui. Update Purpose after archive.
## Requirements
### Requirement: 後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入
系統 SHALL 在使用者導覽至任何 `/admin/*` 路由時檢查目前登入狀態，依頁面分兩類：
- **一般後台頁面（活動、場館、訂單列表與明細、核銷、銷售報表）**：SHALL 要求 Access Token 帶有 `OrganizerId` claim（即已切換至一個 Approved Organizer），未帶者（含未登入、含尚未切換 Organizer 的一般 Member 或 Admin）SHALL 被導向登入頁或「選擇主辦方」頁面，不得進入頁面內容；不論角色是否為 `Admin`，已切換 Organizer 者皆可進入
- **Organizer 審核頁面（`/admin/organizers`）**：SHALL 要求角色為 `Admin`，不要求已切換 Organizer

#### Scenario: AWU-GUARD-001 未登入使用者直接進入後台路由
- **WHEN** 未登入的使用者直接開啟任一 `/admin/*` 網址
- **THEN** 系統導向登入頁，不顯示後台頁面內容

#### Scenario: AWU-GUARD-002 已登入但尚未切換 Organizer 的使用者進入一般後台頁面
- **WHEN** 已登入、但 Access Token 未帶 `OrganizerId` claim 的使用者開啟活動、場館、訂單、核銷或銷售報表頁面
- **THEN** 系統導向「選擇主辦方」頁面，不顯示該後台頁面內容

#### Scenario: AWU-GUARD-003 已切換 Organizer 後可進入一般後台頁面
- **WHEN** 已成功切換至一個 Approved Organizer 的使用者開啟活動、場館、訂單、核銷或銷售報表頁面
- **THEN** 系統顯示對應後台頁面內容

#### Scenario: AWU-GUARD-004 非 Admin 角色嘗試進入審核頁面
- **WHEN** 角色非 `Admin` 的已登入使用者（不論是否已切換 Organizer）開啟 `/admin/organizers` 審核頁面
- **THEN** 系統導向買家端首頁，不顯示審核頁面內容

#### Scenario: AWU-GUARD-005 Admin 角色可進入審核頁面，不需切換 Organizer
- **WHEN** 角色為 `Admin` 的使用者開啟 `/admin/organizers` 審核頁面，且尚未切換至任何 Organizer
- **THEN** 系統顯示審核頁面內容

#### Scenario: AWU-GUARD-007 已切換 Organizer 但角色非 Admin 的使用者可進入訂單、核銷與銷售報表頁面
- **WHEN** 已切換至一個 Approved Organizer、但角色不是 `Admin` 的使用者開啟訂單列表、訂單明細、核銷或銷售報表頁面
- **THEN** 系統顯示對應後台頁面內容

### Requirement: Admin 可透過介面管理場館與座位圖
系統 SHALL 提供場館列表頁與建立場館／座位圖的表單，呼叫既有 `event-management` API 完成建立。場館列表 SHALL 透過場地查詢 API 取得目前資料庫中所有場館的真實資料，重新整理頁面後清單 SHALL 保留（不再是僅存於瀏覽器分頁 session 的暫存清單）；建立場館或座位圖成功後，系統 SHALL 重新呼叫查詢 API 刷新列表，不依賴任何前端快取層。Admin 點選場館列表中的某一列時，SHALL 顯示該場館底下的座位圖摘要（Id、座位數）；快速連續點選不同場館時，只有對應目前選定場館的查詢回應可以套用，較晚抵達但對應較舊選擇的回應 MUST 被捨棄（比照建立活動表單場館下拉選單的過期回應防護）。座位圖摘要清單中，Admin SHALL 可以展開任一座位圖查看其完整座位清單（分區代碼＋座位號碼）。建立座位圖表單 SHALL 同時支援「手動新增單一座位」與「批次產生」兩種輸入方式——批次產生以分區代碼＋起始號碼＋結束號碼一次展開成整批座位，供大量連號座位使用；兩種方式產生的座位在同一次建立座位圖時 SHALL 可以合併送出。

#### Scenario: 建立場館
- **WHEN** Admin 在建立場館表單填寫有效名稱並送出
- **THEN** 系統呼叫建立場館 API 成功，重新查詢場館列表，新場館出現在列表中

#### Scenario: 重新整理頁面後場館列表仍保留
- **WHEN** Admin 建立場館後重新整理瀏覽器頁面
- **THEN** 系統透過查詢 API 重新取得場館列表，剛建立的場館仍顯示在列表中

#### Scenario: 手動新增少量座位並建立座位圖
- **WHEN** Admin 在某場館下用「手動新增」逐一輸入少量座位並送出
- **THEN** 系統呼叫建立座位圖 API 成功，重新查詢該場館明細，新座位圖出現在座位圖清單中

#### Scenario: 批次產生大量座位並建立座位圖
- **WHEN** Admin 在「批次產生」輸入分區代碼、起始號碼、結束號碼並加入這批，重複數次後送出建立座位圖
- **THEN** 系統把所有批次展開成個別座位物件，一次呼叫建立座位圖 API，成功後重新查詢該場館明細，顯示新座位圖與其座位總數

#### Scenario: 快速連續點選不同場館
- **WHEN** Admin 快速點選場館 A 後又立刻點選場館 B，場館 B 的查詢回應先抵達、場館 A 的查詢回應較晚抵達
- **THEN** 系統顯示的座位圖摘要 MUST 對應場館 B，不得被較晚抵達的場館 A 回應覆蓋

#### Scenario: 展開座位圖查看完整座位清單
- **WHEN** Admin 在座位圖摘要清單中展開某一張座位圖
- **THEN** 系統呼叫座位圖明細查詢 API，顯示該座位圖下每個座位的分區代碼與座位號碼

### Requirement: Admin 可透過介面管理活動與票種
系統 SHALL 提供活動列表頁與建立活動／票種的表單，呼叫既有 `event-management` API 完成建立；活動列表頁 SHALL 改為呼叫 Admin 專用的活動列表查詢端點（不再重用買家端也在用的公開 `GET /api/events` API），取得建立者、建立時間與售票狀況統計。「建立活動」表單 SHALL 為獨立頁面，不再是活動列表頁下方的內嵌表單；活動列表頁 SHALL 提供進入該頁面的入口，建立活動成功後 SHALL 導回活動列表頁。建立活動表單的場館欄位 SHALL 為下拉選單，資料來源為場地查詢 API；選定場館後，座位圖欄位 SHALL 顯示該場館底下的座位圖下拉選單（每個選項顯示座位圖 Id 與座位總數，供辨識），資料來源為場地明細查詢 API，不再是手動輸入 GUID 的文字欄位。Admin 已選定座位圖後若改選另一個場館，系統 SHALL 清除已選的座位圖，座位圖下拉選單改顯示新場館底下的座位圖選項，不得保留原場館的座位圖選擇值（避免送出時場館與座位圖分屬不同場地）。活動列表顯示的場館／座位圖欄位 SHALL 顯示原始 Id，不查詢對應名稱；活動列表 SHALL 額外顯示建立者（人類看得懂的顯示名稱，查無對應會員或活動為本次功能上線前建立時顯示為「—」）與建立時間（同樣沒有紀錄時顯示為「—」），以及依 Available／Held／Sold 座位數量比例、顏色區分呈現的售票狀況橫條圖；此橫條圖與活動列表其餘欄位一樣，是頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新。建立活動表單 SHALL 提供「活動說明」（多行文字）、「海報網址」（圖片連結）、「每筆訂單限購張數」（正整數）三個選填欄位；「活動說明」「海報網址」供買家端活動詳情頁顯示，「每筆訂單限購張數」供買家端選位時限制單筆訂單最多可選的座位數，留空代表不限制。這三個欄位都不填也 SHALL 能成功建立活動；若「每筆訂單限購張數」有填寫，SHALL 為正整數，否則系統 SHALL 顯示驗證錯誤、不呼叫建立活動 API。建立票種表單 SHALL 提供「是否綁座位」開關（對應 `RequiresSeat`，預設開啟），開啟時沿用既有分區代碼輸入欄位（標籤「分區代碼」）；關閉時該輸入欄位標籤改為「票種名稱」，且 SHALL 額外顯示「可售總量」正整數輸入欄位（對應 `AvailableQuantity`，必填），未填或填 0／負數時系統 SHALL 顯示驗證錯誤、不呼叫建立票種 API；切換回開啟時 SHALL 清空已輸入的可售總量，不隨表單送出殘留欄位值。票種清單 SHALL 顯示每筆票種是否綁座位，`RequiresSeat = false` 的票種 SHALL 額外顯示可售總量。建立票種表單的票價輸入與活動列表如有顯示票價之處，SHALL 標示「NT$」貨幣單位。「票種名稱」欄位（`RequiresSeat = false` 時的分區代碼欄位）SHALL NOT 額外做長度或空白字元的前端驗證，完全交由既有後端 API 驗證——比照既有「分區代碼」欄位本來就沒有前端格式驗證的既定行為，本次不新增。

#### Scenario: 進入建立活動頁面
- **WHEN** Admin 在活動列表頁點選「建立活動」入口
- **THEN** 系統導向獨立的建立活動頁面，不在活動列表頁內顯示表單

#### Scenario: 建立活動
- **WHEN** Admin 在建立活動頁面的下拉選單選擇場館與座位圖並填寫活動資訊送出
- **THEN** 系統呼叫建立活動 API 成功，導回活動列表頁，列表顯示新活動

#### Scenario: 選擇場館後座位圖下拉選單隨之更新
- **WHEN** Admin 在建立活動表單選擇某個場館
- **THEN** 系統呼叫該場館的明細查詢 API，座位圖下拉選單只顯示該場館底下的座位圖選項

#### Scenario: 切換場館後清除已選座位圖
- **WHEN** Admin 在建立活動表單已選定場館與其下的座位圖，之後改選另一個場館
- **THEN** 系統清除已選的座位圖，座位圖下拉選單改顯示新場館底下的座位圖選項，Admin 須重新選擇座位圖才能送出表單

#### Scenario: 尚未有任何場館或座位圖時無法選擇
- **WHEN** Admin 開啟建立活動表單，但目前資料庫中沒有任何場館（或選定的場館底下沒有任何座位圖）
- **THEN** 系統在對應下拉選單顯示「尚無可選項目」，不允許送出建立活動

#### Scenario: 建立活動時填寫說明、海報網址與限購張數
- **WHEN** Admin 在建立活動表單額外填寫活動說明、海報網址、每筆訂單限購張數並送出
- **THEN** 系統呼叫建立活動 API 成功，這三個欄位隨活動資料一併儲存，買家端活動詳情頁能顯示說明/海報，選位時也會套用限購張數

#### Scenario: 建立活動時不填說明、海報網址與限購張數
- **WHEN** Admin 在建立活動表單留空活動說明、海報網址、每筆訂單限購張數並送出
- **THEN** 系統呼叫建立活動 API 成功，不因為這三個選填欄位空白而驗證失敗，買家端不限制選位張數

#### Scenario: 限購張數填寫非正整數
- **WHEN** Admin 在「每筆訂單限購張數」填寫 0 或負數
- **THEN** 系統顯示驗證錯誤訊息，不呼叫建立活動 API

#### Scenario: 為活動建立座位制票種
- **WHEN** Admin 在某活動下維持「是否綁座位」開關為開啟，填寫分區代碼與票價送出
- **THEN** 系統呼叫建立票種 API（`RequiresSeat = true`）成功，票價輸入欄位旁 SHALL 標示「NT$」貨幣單位

#### Scenario: 為活動建立純計數票種
- **WHEN** Admin 在某活動下關閉「是否綁座位」開關，填寫票種名稱、票價與正整數的可售總量送出
- **THEN** 系統呼叫建立票種 API（`RequiresSeat = false`，帶入 `AvailableQuantity`）成功，票種清單顯示該票種為計數制與其可售總量

#### Scenario: 建立純計數票種未填可售總量
- **WHEN** Admin 關閉「是否綁座位」開關，但可售總量欄位留空或填 0／負數
- **THEN** 系統顯示驗證錯誤訊息，不呼叫建立票種 API

#### Scenario: 切換回座位制時清空可售總量
- **WHEN** Admin 先關閉「是否綁座位」開關並輸入可售總量，之後重新開啟該開關
- **THEN** 系統清空已輸入的可售總量欄位值，送出表單時不帶入該欄位

#### Scenario: 開啟活動列表頁時票種清單正確顯示既有票種的模式與可售總量
- **WHEN** Admin 開啟活動列表頁，展開一個活動底下已存在座位制與計數制票種各一筆的票種清單
- **THEN** 系統依查詢 API 回傳的 `RequiresSeat`／`AvailableQuantity` 顯示對應的模式標籤，計數制票種額外顯示可售總量數字，座位制票種該欄位顯示「—」

#### Scenario: 活動列表顯示建立者與建立時間
- **WHEN** Admin 開啟活動列表頁，列表中有本次功能上線後建立的活動
- **THEN** 系統顯示該活動的建立者顯示名稱與建立時間

#### Scenario: 活動列表顯示本次功能上線前建立的舊活動
- **WHEN** Admin 開啟活動列表頁，列表中有本次功能上線前就存在、沒有建立者/建立時間紀錄的活動
- **THEN** 系統對應欄位顯示「—」，不得顯示誤導性的預設值（例如空白日期或無意義的 0）

#### Scenario: 活動列表顯示售票狀況橫條圖
- **WHEN** Admin 開啟活動列表頁
- **THEN** 系統為每筆活動顯示依 Available／Held／Sold 座位數量比例、顏色區分的橫條圖

### Requirement: 掃描期間與相機不可用時皆可切換到手動輸入 Ticket ID 完成核銷
系統 SHALL 在核銷掃碼頁面提供手動輸入 Ticket ID 的方式，不僅限於相機完全不可用時才提供：相機正常運作、正在掃描中時，畫面 SHALL 同時提供切換到手動輸入的操作，供 QR Code 印刷模糊或毀損等相機仍運作但無法辨識單一票券的情況使用；裝置不支援相機掃描能力、無相機裝置、使用者拒絕相機權限，或相機初始化發生非預期錯誤時，系統 SHALL 直接以手動輸入表單為畫面主體。裝置不支援相機掃描能力的情況下，系統 SHALL NOT 提供重新嘗試相機的操作（此情況不會因為重試而改變）；無相機裝置、權限被拒絕、或非預期錯誤三種情況下，系統 SHALL 提供重新嘗試相機的操作，讓操作者主動決定是否再次嘗試初始化相機。手動輸入僅接受純 Ticket ID（不含簽章、不含分隔符），系統 SHALL 在送出前於前端驗證格式為合法 GUID，格式不合法時 SHALL 直接顯示「Ticket ID 格式不正確」並阻擋送出，不得呼叫核銷端點；此前端格式檢查僅用於避免不必要的呼叫，不構成安全驗證。格式合法送出後，系統 SHALL 呼叫核銷端點並將 `signature` 欄位固定帶 `null`（不進行簽章驗證），並依回應顯示與掃描路徑一致的結果（成功／已核銷過／查無此票／系統錯誤）。系統 SHALL 在介面上明確標示「掃描核銷」與「手動輸入」兩種操作方式的性質差異（掃描核銷經過簽章驗證；手動輸入為操作人員信任操作、未經簽章驗證），避免操作者誤以為兩者提供相同的真偽保證；介面標示文字 SHALL NOT 暗示只有平台 `Admin`能執行手動輸入（可執行者為任何已切換 Organizer 的成員，且只能核銷目前 Organizer 名下的票券，見 `ticket-redemption` 能力）。裝置不支援相機掃描能力、無相機裝置、使用者拒絕相機權限、相機初始化發生非預期錯誤四種情況 SHALL 分別顯示可理解、彼此不同的原因說明文字（例如「此瀏覽器不支援相機掃描」「找不到可用相機」「相機權限被拒絕」「相機初始化發生錯誤」），不得共用同一句籠統訊息；提供「重新嘗試相機」操作的三種情況（無相機裝置、權限被拒絕、非預期錯誤）中，若重試後仍然失敗，系統 SHALL 停留在手動輸入表單並依新的失敗原因更新說明文字與「重新嘗試相機」操作是否可用，不得卡在載入中畫面或遺失手動輸入能力。

#### Scenario: ADMIN-REDEEM-MANUAL-SWITCH 掃描期間主動切換到手動輸入
- **WHEN** 操作人員在相機運作正常的掃描畫面中，因 QR Code 印刷模糊或毀損無法被辨識
- **THEN** 系統提供切換到手動輸入的操作，不需等待相機判定失敗

#### Scenario: ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED 裝置不支援相機掃描能力時直接顯示手動輸入
- **WHEN** 操作人員開啟核銷掃碼頁面，裝置不支援相機掃描能力（例如非安全連線環境）
- **THEN** 系統以手動輸入表單為畫面主體，不提供重新嘗試相機的操作，操作人員仍可完成核銷

#### Scenario: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE 相機不可用但可重新嘗試的情況顯示手動輸入與重試操作
- **WHEN** 操作人員開啟核銷掃碼頁面時沒有可用相機、使用者拒絕相機權限請求，或相機初始化發生非預期錯誤
- **THEN** 系統以手動輸入表單為畫面主體，並提供重新嘗試相機的操作，操作人員仍可完成核銷

#### Scenario: ADMIN-REDEEM-MANUAL-SUCCESS 手動輸入核銷成功
- **WHEN** 操作人員在手動輸入欄位填入一個狀態為 `Issued` 的合法 Ticket ID 並送出
- **THEN** 系統呼叫核銷端點成功（`signature` 為 `null`），顯示核銷成功結果

#### Scenario: ADMIN-REDEEM-MANUAL-CONFLICT 手動輸入已核銷過的 Ticket ID
- **WHEN** 操作人員手動輸入一個狀態已是 `Redeemed` 的合法 Ticket ID 並送出
- **THEN** 系統顯示「已核銷過」的衝突結果

#### Scenario: ADMIN-REDEEM-MANUAL-NOT-FOUND 手動輸入查無此票的 Ticket ID
- **WHEN** 操作人員手動輸入一個格式合法但系統中不存在的 Ticket ID 並送出
- **THEN** 系統顯示「查無此票」結果

#### Scenario: ADMIN-REDEEM-MANUAL-SYSTEM-ERROR 手動輸入時遇到非預期系統錯誤
- **WHEN** 操作人員手動輸入合法 Ticket ID 送出後，核銷端點回應非預期系統錯誤（例如網路中斷、5xx）
- **THEN** 系統顯示可重試的通用系統錯誤，不得顯示為「查無此票」，且不得自動重試核銷呼叫

#### Scenario: ADMIN-REDEEM-MANUAL-INVALID-FORMAT 手動輸入格式不正確的內容
- **WHEN** 操作人員在手動輸入欄位填入非合法 GUID 格式的內容並嘗試送出
- **THEN** 系統直接顯示「Ticket ID 格式不正確」，不呼叫核銷端點

#### Scenario: ADMIN-REDEEM-MANUAL-RETRY-CAMERA-STILL-FAILS 重新嘗試相機後仍然失敗
- **WHEN** 操作人員在手動輸入畫面點選「重新嘗試相機」，但相機初始化再次失敗（原因可能與前一次不同）
- **THEN** 系統維持顯示手動輸入表單，說明文字更新為本次失敗的原因，不卡在載入中畫面，操作人員仍可繼續用手動輸入完成核銷

#### Scenario: ADMIN-REDEEM-TRUST-LABEL 介面標示兩種核銷方式的信任差異
- **WHEN** 操作人員開啟核銷掃碼頁面，切換於掃描與手動輸入兩種模式之間
- **THEN** 系統在介面上分別標示兩者的性質差異（掃描核銷經簽章驗證；手動輸入未經簽章驗證，屬操作人員信任操作）

### Requirement: 使用者可透過介面申請建立主辦方
系統 SHALL 提供申請建立主辦方的表單頁面，供已登入使用者填寫主辦方名稱並呼叫 `organizer-management` 能力的申請端點。申請成功後，系統 SHALL 立即呼叫切換操作情境端點——呼叫時 MUST 一併帶入使用者目前持有的 Refresh Token（比照既有 `/api/auth/refresh` 流程既有的儲存機制，不新增儲存），與一般「我的主辦方」清單頁的切換操作使用同一個前端呼叫邏輯（見「使用者可透過介面查看與切換自己所屬的主辦方」需求）。但因新申請預設狀態為 `Pending`，這次自動切換 MUST 被後端依 `organizer-management` 能力的 `ORG-APPLY-005` 規則拒絕；介面 SHALL 依此預期的拒絕結果顯示「申請已送出，待平台審核」，並導向「我的主辦方」清單頁，不導向一般後台頁面，也 MUST NOT 因為自動切換呼叫失敗而顯示錯誤訊息（因為對這個流程而言，切換被拒絕是預期行為，不是異常）。若自動切換呼叫因非預期原因失敗（例如網路逾時、伺服器錯誤，而非 `ORG-APPLY-005` 的預期拒絕），介面 SHALL 仍正常顯示「申請已送出，待平台審核」並導向清單頁（申請本身已成功，自動切換只是附帶的使用者體驗優化，不影響申請結果的呈現，見 `AWU-APPLY-004`）。

#### Scenario: AWU-APPLY-001 申請建立主辦方成功
- **WHEN** 已登入使用者在申請表單填寫有效名稱並送出
- **THEN** 系統呼叫申請端點成功

#### Scenario: AWU-APPLY-002 申請成功後自動嘗試切換，因 Pending 被拒絕
- **WHEN** AWU-APPLY-001 的申請成功後，系統立即帶著使用者目前持有的 Refresh Token 呼叫切換操作情境端點
- **THEN** 後端依新申請的 `Pending` 狀態拒絕這次切換；介面 SHALL 顯示「申請已送出，待平台審核」，導向「我的主辦方」清單頁，MUST NOT 顯示錯誤訊息，也 MUST NOT 導向一般後台頁面

#### Scenario: AWU-APPLY-003 申請表單名稱留空
- **WHEN** 已登入使用者在申請表單留空名稱並嘗試送出
- **THEN** 系統顯示驗證錯誤，不呼叫申請端點

#### Scenario: AWU-APPLY-004 申請成功後自動嘗試切換，因非預期原因失敗
- **WHEN** AWU-APPLY-001 的申請成功後，系統立即呼叫切換操作情境端點，但該次呼叫因網路逾時或伺服器錯誤等非預期原因失敗（並非 `ORG-APPLY-005` 的預期拒絕）
- **THEN** 介面 SHALL 仍顯示「申請已送出，待平台審核」，導向「我的主辦方」清單頁，MUST NOT 顯示錯誤訊息

### Requirement: 使用者可透過介面查看與切換自己所屬的主辦方
系統 SHALL 提供「我的主辦方」清單頁，顯示使用者目前所屬的所有 Organizer 及其狀態（`Pending`／`Approved`／`Rejected`／`Suspended`）。狀態為 `Approved` 的項目 SHALL 提供「切換」操作，呼叫切換操作情境端點時 MUST 一併帶入使用者目前持有的 Refresh Token，成功後系統 SHALL 導向一般後台首頁；非 `Approved` 狀態的項目 SHALL 顯示對應狀態說明文字，不提供切換操作。導覽列 SHALL 顯示使用者目前切換所在的 Organizer 名稱（尚未切換時顯示提示文字），並提供快速前往「我的主辦方」清單頁的入口。

#### Scenario: AWU-LIST-001 查看所屬主辦方清單
- **WHEN** 已登入使用者開啟「我的主辦方」清單頁
- **THEN** 系統顯示該使用者目前所屬的所有 Organizer 及其狀態

#### Scenario: AWU-LIST-002 切換到已核准的主辦方
- **WHEN** 已登入使用者在清單頁對一筆狀態為 `Approved` 的 Organizer 點選「切換」
- **THEN** 系統帶著使用者目前持有的 Refresh Token 呼叫切換操作情境端點成功，導向一般後台首頁，導覽列顯示該 Organizer 名稱

#### Scenario: AWU-LIST-003 待審核、已駁回或已停權的主辦方不提供切換操作
- **WHEN** 已登入使用者在清單頁查看一筆狀態為 `Pending`、`Rejected` 或 `Suspended` 的 Organizer
- **THEN** 系統顯示對應狀態說明文字，不提供「切換」操作

#### Scenario: AWU-LIST-004 尚未加入任何主辦方
- **WHEN** 已登入使用者的「我的主辦方」清單為空
- **THEN** 系統顯示提示文字與前往申請表單的入口，不顯示錯誤

### Requirement: 平台管理員可透過介面審核主辦方申請
系統 SHALL 在 `/admin/organizers` 提供待審核主辦方清單頁，依「後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入」Requirement 的審核頁面規則保護（僅限角色為 `Admin`，不要求已切換 Organizer）。清單 SHALL 顯示每筆待審核申請的主辦方名稱與申請人；每筆 SHALL 提供「核准」與「駁回」操作，呼叫對應端點成功後，系統 SHALL 重新查詢清單，該筆申請自清單移除。

#### Scenario: AWU-REVIEW-001 查看待審核主辦方清單
- **WHEN** `Admin` 角色使用者開啟 `/admin/organizers` 頁面
- **THEN** 系統顯示目前所有 `Pending` 狀態的主辦方申請與其申請人

#### Scenario: AWU-REVIEW-002 核准主辦方申請
- **WHEN** `Admin` 角色使用者對清單中一筆申請點選「核准」
- **THEN** 系統呼叫核准端點成功，重新查詢清單，該筆申請自待審核清單移除

#### Scenario: AWU-REVIEW-003 駁回主辦方申請
- **WHEN** `Admin` 角色使用者對清單中一筆申請點選「駁回」
- **THEN** 系統呼叫駁回端點成功，重新查詢清單，該筆申請自待審核清單移除

### Requirement: 主辦方成員可查看目前 Organizer 名下的訂單列表與明細
系統 SHALL 提供訂單列表頁與訂單詳情頁，呼叫 `order-administration` API 顯示呼叫端目前切換所在 Organizer 名下活動的訂單狀態，與單筆訂單內的座位項目明細；不顯示其他 Organizer 名下活動的訂單（過濾由後端 `order-administration` 能力執行，前端不另行過濾）。此狀態為頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新。訂單列表與詳情頁的訂單狀態 SHALL 以中文標籤顯示，標籤呈現方式、顏色、對照規則與未知值處理比照 `buyer-web-ui` 能力「「我的訂單」列表與明細頁串接查詢 API，顯示訂單、票券狀態與 QR Code」Requirement；持有到期時間 SHALL 僅在訂單狀態為 Pending 時顯示，其他狀態 MUST NOT 顯示，理由同該 Requirement（`HeldUntilUtc` 為不因終態改寫的原始值）。

#### Scenario: AWU-ORDER-LIST-001 查看目前 Organizer 名下的訂單列表
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟後台訂單列表頁
- **THEN** 系統顯示該 Organizer 名下活動目前的訂單與其中文狀態標籤

#### Scenario: AWU-ORDER-DETAIL-001 查看訂單明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者點選某筆訂單進入詳情頁
- **THEN** 系統顯示該訂單內的每一筆座位項目明細

#### Scenario: AWU-ORDER-HOLD-001 持有到期時間僅於 Pending 訂單顯示
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟訂單列表，列表中同時有 Pending 與 Paid 訂單，並分別進入兩筆的詳情頁
- **THEN** 列表與詳情頁皆只對 Pending 訂單顯示持有到期時間，Paid 訂單不顯示

### Requirement: 已切換 Organizer 的操作人員可透過介面掃描 QR Code 核銷票券
本 Requirement 與下一條「掃描期間與相機不可用時皆可切換到手動輸入 Ticket ID 完成核銷」Requirement 中的「操作人員」，指任何已切換至一個 Approved Organizer 的使用者（不限 `Admin` 角色），只能核銷目前 Organizer 名下的票券（見 `ticket-redemption` 能力）。系統 SHALL 在後台提供核銷掃碼頁面，使用裝置相機掃描票券 QR Code；掃描到內容後，系統 SHALL 依 `ticket-issuance` 能力定義的精確格式解析出 Ticket ID 與簽章，並呼叫核銷端點（`PATCH /api/admin/tickets/{id}/redeem`，含 `signature` 欄位，值為解析出的簽章）完成核銷——解析出的 `ticketId`／`signature` MUST 原封不動送入該次呼叫，不得在中途被轉換或省略。系統 SHALL 依核銷端點回應顯示可分辨的結果，且不得僅以顏色區分：成功、已核銷過（狀態衝突）、查無此票、簽章無效（含格式不合法）、以及非上述已知情況的系統錯誤。頁面切到背景（例如切換分頁或應用程式）後再切回前景時，系統 SHALL 讓相機掃描恢復可正常運作，不得停留在「畫面顯示可掃描但實際上無法偵測」的不一致狀態，也不得因為背景/前景切換而重複觸發核銷呼叫；切背景當下若正在等待某次核銷呼叫的結果，系統 SHALL 讓該次呼叫正常完成並在切回前景時顯示其結果，不得重新發送同一次核銷請求。查無此票、簽章無效、無法辨識與系統錯誤四類結果 SHALL 以比成功結果更高的通知急迫程度呈現（例如更長停留時間與可被輔助科技優先朗讀的呈現方式，實際秒數為實作層級的可調整預設值，不在本需求的驗收範圍內），供操作者留意。核銷結果顯示後，系統 SHALL 於一段時間後自動恢復可掃描狀態，或提供操作者可立即恢復的操作，不需重新整理頁面或手動導覽即可繼續掃描下一張票；因系統錯誤失敗的票券，操作者恢復掃描後 SHALL 能立即重新嘗試同一張票，不得被任何重複偵測機制永久阻擋。結果顯示期間，系統 MUST NOT 因相機持續偵測到相同或殘留的 QR 內容而重複呼叫核銷端點；此重複偵測抑制僅限於單一輪次的結果顯示期間有效，恢復可掃描狀態後 MUST NOT 沿用至下一輪。掃描到的內容若不符合預期格式，系統 SHALL 顯示「無法辨識的票券內容」，不呼叫核銷端點；此格式檢查僅用於避免不必要的呼叫，核銷端點本身仍會對任何內容做最終驗證。此頁面依「後台路由僅限已切換 Organizer 或審核頁面的 操作人員進入」Requirement 的一般後台頁面規則保護（要求已切換至一個 Approved Organizer，不限 `Admin` 角色），不額外定義權限規則；可核銷的票券範圍由後端 `ticket-redemption` 能力依呼叫端目前 Organizer 限制。系統 SHALL 在後台導覽選單提供進入此頁面的入口。

#### Scenario: ADMIN-REDEEM-SCAN-SUCCESS 掃描成功核銷
- **WHEN** 操作人員用相機掃到一張狀態為 `Issued` 的票券 QR Code，簽章驗證通過
- **THEN** 系統呼叫核銷端點成功，顯示核銷成功結果

#### Scenario: ADMIN-REDEEM-SCAN-DISPATCH 解析結果正確送入核銷呼叫
- **WHEN** 掃描到內容 `{ticketId}.{signature}` 且格式合法
- **THEN** 系統呼叫核銷端點時，`id` 路徑參數為解析出的 `ticketId`、request body 的 `signature` 欄位為解析出的 `signature`，兩者皆與掃描內容一致，不被修改或遺漏

#### Scenario: ADMIN-REDEEM-SCAN-CONFLICT 掃描已核銷過的票券
- **WHEN** 操作人員掃到的票券簽章驗證通過，但目前狀態已是 `Redeemed`
- **THEN** 系統顯示「已核銷過」的衝突結果，不視為系統錯誤

#### Scenario: ADMIN-REDEEM-SCAN-NOT-FOUND 掃描查無此票的內容
- **WHEN** 掃描到的內容格式正確、簽章驗證通過，但解析出的 Ticket ID 在系統中不存在
- **THEN** 系統顯示「查無此票」結果

#### Scenario: ADMIN-REDEEM-SCAN-INVALID-SIGNATURE 掃描到簽章被竄改的內容
- **WHEN** 掃描到的內容格式正確（含分隔符、前段為合法 GUID），但後端驗證簽章與內容不符
- **THEN** 系統呼叫核銷端點回應簽章無效，顯示「簽章驗證失敗」結果，不視為查無此票或已核銷過

#### Scenario: ADMIN-REDEEM-SCAN-UNRECOGNIZED 掃描到無法辨識的內容
- **WHEN** 掃描到的內容不含分隔符，分隔符前段不是合法 GUID 格式，或分隔符後段（簽章）為空
- **THEN** 系統顯示「無法辨識的票券內容」，不呼叫核銷端點

#### Scenario: ADMIN-REDEEM-SCAN-SYSTEM-ERROR 核銷呼叫遇到非預期系統錯誤
- **WHEN** 核銷端點回應非 200 系列、非 404、非 409、非簽章無效的錯誤（例如網路中斷、5xx、換發後仍未授權）
- **THEN** 系統顯示可重試的通用系統錯誤，不得顯示為「查無此票」或任何暗示票券本身有問題的訊息，且不得自動重試核銷呼叫

#### Scenario: ADMIN-REDEEM-SCAN-RETRY-AFTER-ERROR 系統錯誤後可立即重新嘗試同一張票
- **WHEN** 前一次掃描同一張票因系統錯誤失敗，操作者恢復掃描狀態後鏡頭再次對準同一張票（QR 內容相同）
- **THEN** 系統重新呼叫核銷端點，不因「內容與上次相同」而略過此次嘗試

#### Scenario: ADMIN-REDEEM-SCAN-AUTO-RESUME 核銷完成後可連續掃描下一張
- **WHEN** 核銷結果顯示後經過系統設定的停留時間，或操作者主動點選繼續
- **THEN** 系統恢復到可掃描狀態，不需重新整理頁面或手動導覽

#### Scenario: ADMIN-REDEEM-SCAN-DEDUPE 結果顯示期間忽略重複偵測
- **WHEN** 核銷結果橫幅顯示中，相機仍持續偵測到與本次核銷相同或殘留的 QR 內容
- **THEN** 系統不因此重複呼叫核銷端點

#### Scenario: ADMIN-REDEEM-BACKGROUND-RESUME 背景切回前景後相機恢復正常運作
- **WHEN** 操作人員在掃描畫面中把頁面切到背景（例如切換分頁），一段時間後切回前景
- **THEN** 相機掃描恢復可正常運作，不停留在無法偵測的假掃描畫面，也不因此重複觸發任何核銷呼叫

#### Scenario: ADMIN-REDEEM-BACKGROUND-PROCESSING-COMPLETES 切背景時進行中的核銷呼叫正常完成
- **WHEN** 核銷呼叫進行中時操作人員把頁面切到背景，稍後切回前景
- **THEN** 系統顯示該次呼叫的實際結果，不重新發送同一次核銷請求

#### Scenario: ADMIN-REDEEM-NAV-ENTRY 後台導覽可進入核銷頁面
- **WHEN** 操作人員開啟後台，查看導覽選單
- **THEN** 選單中 SHALL 有可點選進入核銷掃碼頁面的項目

### Requirement: 後台導覽入口依頁面權限規則顯示
後台導覽選單與頁面內的功能入口 SHALL 與其目標頁面的路由守衛規則一致，避免顯示「點了只會被導回其他頁面」的入口：
- 「場館管理」「活動管理」「訂單管理」「票券核銷」選單項目 SHALL 對所有能進入後台版面的使用者顯示，進入後由路由守衛依是否已切換 Organizer 把關
- 「主辦方審核」選單項目 SHALL 僅對角色為 `Admin` 的使用者顯示
- 活動列表中每筆活動的「銷售報表」入口 SHALL 對可進入活動列表的使用者顯示（活動列表本身已要求已切換 Organizer，且只列出目前 Organizer 名下的活動），不再依 `Admin` 角色隱藏

#### Scenario: AWU-NAV-001 非 Admin 的 Organizer 成員看得到訂單與核銷選單、看不到審核選單
- **WHEN** 已切換至一個 Approved Organizer、角色非 `Admin` 的使用者進入後台
- **THEN** 導覽選單顯示「場館管理」「活動管理」「訂單管理」「票券核銷」，不顯示「主辦方審核」

#### Scenario: AWU-NAV-002 已切換 Organizer 的 Admin 看得到一般選單與審核選單
- **WHEN** 角色為 `Admin`、且已切換至一個 Approved Organizer 的使用者進入後台
- **THEN** 導覽選單顯示「場館管理」「活動管理」「訂單管理」「票券核銷」，另顯示「主辦方審核」

#### Scenario: AWU-NAV-004 尚未切換 Organizer 的 Admin 在審核頁仍看得到一般選單
- **WHEN** 角色為 `Admin`、尚未切換至任何 Organizer 的使用者開啟 `/admin/organizers` 審核頁
- **THEN** 導覽選單同樣顯示「場館管理」「活動管理」「訂單管理」「票券核銷」「主辦方審核」，一般選單項目不因尚未切換 Organizer 而隱藏；點選任一一般後台項目時，依 `AWU-GUARD-002` 導向「選擇主辦方」頁面

#### Scenario: AWU-NAV-003 非 Admin 的 Organizer 成員看得到活動的銷售報表入口
- **WHEN** 已切換至一個 Approved Organizer、角色非 `Admin` 的使用者開啟活動列表
- **THEN** 每筆活動皆顯示「銷售報表」入口，點選後可進入該活動的銷售報表頁

