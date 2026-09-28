## RENAMED Requirements

- FROM: `### Requirement: Admin 可查看所有訂單列表與明細`
- TO: `### Requirement: 主辦方成員可查看目前 Organizer 名下的訂單列表與明細`
- FROM: `### Requirement: Admin 可透過介面掃描 QR Code 核銷票券`
- TO: `### Requirement: 已切換 Organizer 的操作人員可透過介面掃描 QR Code 核銷票券`

## MODIFIED Requirements

### Requirement: 後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入
系統 SHALL 在使用者導覽至任何 `/admin/*` 路由時檢查目前登入狀態，依頁面分兩類：
- **一般後台頁面（活動、場館、訂單列表與明細、核銷、銷售報表）**：SHALL 要求 Access Token 帶有 `OrganizerId` claim（即已切換至一個 Approved Organizer），未帶者（含未登入、含尚未切換 Organizer 的一般 Member 或 Admin）SHALL 被導向登入頁或「選擇主辦方」頁面，不得進入頁面內容；不論角色是否為 `Admin`，已切換 Organizer 者皆可進入
- **Organizer 審核頁面（`/admin/organizers`）**：SHALL 要求角色為 `Admin`，不要求已切換 Organizer

**本次變更**：訂單列表與明細、核銷、銷售報表頁面從 `event-management-organizer-scoping` 遺留的「維持既有角色為 Admin」規則，改為與活動、場館頁面相同的 `OrganizerId` claim 規則；原 `AWU-GUARD-006`（訂單、核銷頁面仍要求 Admin 角色）隨之移除，由 `AWU-GUARD-007` 取代。

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
- **THEN** 系統顯示對應後台頁面內容（本次變更前會被導向買家端首頁，本次變更後放行）

### Requirement: 主辦方成員可查看目前 Organizer 名下的訂單列表與明細
系統 SHALL 提供訂單列表頁與訂單詳情頁，呼叫 `order-administration` API 顯示呼叫端目前切換所在 Organizer 名下活動的訂單狀態，與單筆訂單內的座位項目明細；不顯示其他 Organizer 名下活動的訂單（過濾由後端 `order-administration` 能力執行，前端不另行過濾）。此狀態為頁面載入或手動重新整理當下查詢 API 取得的結果，非伺服器推播的即時更新。

#### Scenario: AWU-ORDER-LIST-001 查看目前 Organizer 名下的訂單列表
- **WHEN** 已切換至一個 Approved Organizer 的使用者開啟後台訂單列表頁
- **THEN** 系統顯示該 Organizer 名下活動目前的訂單與其狀態

#### Scenario: AWU-ORDER-DETAIL-001 查看訂單明細
- **WHEN** 已切換至一個 Approved Organizer 的使用者點選某筆訂單進入詳情頁
- **THEN** 系統顯示該訂單內的每一筆座位項目明細

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

## ADDED Requirements

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
