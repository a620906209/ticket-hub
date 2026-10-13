## MODIFIED Requirements

### Requirement: 後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入
系統 SHALL 在使用者導覽至任何 `/admin/*` 或 `/platform/*` 路由時檢查目前登入狀態，依頁面分兩類：
- **一般後台頁面（活動、場館、訂單列表與明細、核銷、銷售報表）**：SHALL 要求 Access Token 帶有 `OrganizerId` claim（即已切換至一個 Approved Organizer），未帶者（含未登入、含尚未切換 Organizer 的一般 Member 或 Admin）SHALL 被導向登入頁或「選擇主辦方」頁面，不得進入頁面內容；不論角色是否為 `Admin`，已切換 Organizer 者皆可進入
- **Organizer 審核頁面（`/platform/organizers`）**：SHALL 要求角色為 `Admin`，不要求已切換 Organizer

舊審核頁網址 `/admin/organizers` 與 `/platform` SHALL 重新導向 `/platform/organizers`，重新導向後的目標同樣經過上述檢查。

#### Scenario: AWU-GUARD-001 未登入使用者直接進入後台路由
- **WHEN** 未登入的使用者直接開啟任一 `/admin/*` 或 `/platform/*` 網址
- **THEN** 系統導向登入頁，不顯示後台頁面內容

#### Scenario: AWU-GUARD-002 已登入但尚未切換 Organizer 的使用者進入一般後台頁面
- **WHEN** 已登入、但 Access Token 未帶 `OrganizerId` claim 的使用者開啟活動、場館、訂單、核銷或銷售報表頁面
- **THEN** 系統導向「選擇主辦方」頁面，不顯示該後台頁面內容

#### Scenario: AWU-GUARD-003 已切換 Organizer 後可進入一般後台頁面
- **WHEN** 已成功切換至一個 Approved Organizer 的使用者開啟活動、場館、訂單、核銷或銷售報表頁面
- **THEN** 系統顯示對應後台頁面內容

#### Scenario: AWU-GUARD-004 非 Admin 角色嘗試進入審核頁面
- **WHEN** 角色非 `Admin` 的已登入使用者（不論是否已切換 Organizer）開啟 `/platform/organizers`，或開啟舊網址 `/admin/organizers`
- **THEN** 兩者都導向買家端首頁，不顯示審核頁面內容

#### Scenario: AWU-GUARD-005 Admin 角色可進入審核頁面，不需切換 Organizer
- **WHEN** 角色為 `Admin` 的使用者開啟 `/platform/organizers` 審核頁面，且尚未切換至任何 Organizer
- **THEN** 系統顯示審核頁面內容

#### Scenario: AWU-GUARD-007 已切換 Organizer 但角色非 Admin 的使用者可進入訂單、核銷與銷售報表頁面
- **WHEN** 已切換至一個 Approved Organizer、但角色不是 `Admin` 的使用者開啟訂單列表、訂單明細、核銷或銷售報表頁面
- **THEN** 系統顯示對應後台頁面內容

#### Scenario: AWU-GUARD-008 舊審核頁網址與平台管理根路徑導向新審核頁
- **WHEN** 角色為 `Admin`、尚未切換 Organizer 的使用者分別開啟 `/admin/organizers` 與 `/platform`
- **THEN** 兩者最終路徑都是 `/platform/organizers`（路由名稱 `platform-organizers`），並顯示審核頁內容

### Requirement: 使用者可透過介面查看與切換自己所屬的主辦方
系統 SHALL 提供「我的主辦方」清單頁，顯示使用者目前所屬的所有 Organizer 及其狀態（`Pending`／`Approved`／`Rejected`／`Suspended`），以及每筆 Organizer 的「代碼」（Organizer Id 的前 8 個字元），讓同名的 Organizer 可以區分。狀態為 `Approved` 的項目 SHALL 提供「切換」操作，呼叫切換操作情境端點時 MUST 一併帶入使用者目前持有的 Refresh Token，成功後系統 SHALL 導向一般後台首頁；非 `Approved` 狀態的項目 SHALL 顯示對應狀態說明文字，不提供切換操作。導覽列 SHALL 顯示使用者目前切換所在的 Organizer 名稱（尚未切換時顯示提示文字），並提供快速前往「我的主辦方」清單頁的入口。

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

#### Scenario: AWU-LIST-005 同名主辦方以代碼區分
- **WHEN** 已登入使用者的清單中有兩筆名稱相同、Id 不同的 Organizer
- **THEN** 兩列各自顯示自己 Id 的前 8 個字元作為代碼，兩列的代碼不同

### Requirement: 平台管理員可透過介面審核主辦方申請
系統 SHALL 在平台管理區域的 `/platform/organizers` 提供待審核主辦方清單頁，依「後台路由僅限已切換 Organizer 或審核頁面的 Admin 進入」Requirement 的審核頁面規則保護（僅限角色為 `Admin`，不要求已切換 Organizer）。清單 SHALL 顯示每筆待審核申請的主辦方名稱與申請人；每筆 SHALL 提供「核准」與「駁回」操作，呼叫對應端點成功後，系統 SHALL 重新查詢清單，該筆申請自清單移除。

#### Scenario: AWU-REVIEW-001 查看待審核主辦方清單
- **WHEN** `Admin` 角色使用者開啟 `/platform/organizers` 頁面
- **THEN** 系統顯示目前所有 `Pending` 狀態的主辦方申請與其申請人

#### Scenario: AWU-REVIEW-002 核准主辦方申請
- **WHEN** `Admin` 角色使用者對清單中一筆申請點選「核准」
- **THEN** 系統呼叫核准端點成功，重新查詢清單，該筆申請自待審核清單移除

#### Scenario: AWU-REVIEW-003 駁回主辦方申請
- **WHEN** `Admin` 角色使用者對清單中一筆申請點選「駁回」
- **THEN** 系統呼叫駁回端點成功，重新查詢清單，該筆申請自待審核清單移除

### Requirement: 後台導覽入口依頁面權限規則顯示
主辦方後台導覽選單與頁面內的功能入口 SHALL 與其目標頁面的路由守衛規則一致，避免顯示「點了只會被導回其他頁面」的入口：
- 「場館管理」「活動管理」「訂單管理」「票券核銷」選單項目 SHALL 對所有能進入後台版面的使用者顯示，進入後由路由守衛依是否已切換 Organizer 把關
- 主辦方後台導覽選單 MUST NOT 包含「主辦方審核」；平台管理功能只從區域切換選單進入（見「三個區域的版面可由區域名稱與背景色辨識，並可由區域切換選單互相前往」Requirement）
- 活動列表中每筆活動的「銷售報表」入口 SHALL 對可進入活動列表的使用者顯示（活動列表本身已要求已切換 Organizer，且只列出目前 Organizer 名下的活動），不再依 `Admin` 角色隱藏

#### Scenario: AWU-NAV-001 非 Admin 的 Organizer 成員看得到訂單與核銷選單、看不到審核選單
- **WHEN** 已切換至一個 Approved Organizer、角色非 `Admin` 的使用者進入後台
- **THEN** 導覽選單顯示「場館管理」「活動管理」「訂單管理」「票券核銷」，不顯示「主辦方審核」

#### Scenario: AWU-NAV-002 已切換 Organizer 的 Admin 在主辦方後台看不到審核選單
- **WHEN** 角色為 `Admin`、且已切換至一個 Approved Organizer 的使用者進入後台
- **THEN** 導覽選單依序恰為「場館管理」「活動管理」「訂單管理」「票券核銷」，不顯示「主辦方審核」

#### Scenario: AWU-NAV-003 非 Admin 的 Organizer 成員看得到活動的銷售報表入口
- **WHEN** 已切換至一個 Approved Organizer、角色非 `Admin` 的使用者開啟活動列表
- **THEN** 每筆活動皆顯示「銷售報表」入口，點選後可進入該活動的銷售報表頁

### Requirement: 後台導覽在窄螢幕收合為抽屜選單，且 320 至 1280 CSS px 寬時不溢出
後台導覽列 SHALL 依視窗寬度切換版面，且在 320 ≤ W ≤ 1280 時 MUST 不溢出。本 Requirement 中的「W」指瀏覽器視窗的版面寬度（CSS px，不含捲軸）；版面切換以 CSS media query 判定，其寬度含傳統捲軸，因此有傳統捲軸的桌面瀏覽器在 W 約 706～720 時可能已顯示寬螢幕版面，這個區間的版面切換位置不在本需求的保證範圍內，但仍須不溢出；「導覽列不溢出」指導覽列內每個元素的右緣都不超過 W，且除了以截斷方式顯示的主辦方名稱以外，沒有元素的內容寬度超過自身外框寬度。

W ≤ 720 時，後台導覽列 SHALL 只顯示選單按鈕、區域切換選單與目前主辦方名稱入口，水平選單 MUST NOT 顯示；選單按鈕的觸控區 MUST 至少 44×44 CSS px，並提供無障礙名稱「開啟後台選單」與表示抽屜開關狀態的 `aria-expanded`。W > 720 時，系統 SHALL 顯示水平選單，選單按鈕 MUST NOT 顯示。320 ≤ W ≤ 1280 時，導覽列 MUST 不溢出（W < 320 與 W > 1280 不在本需求的保證與驗收範圍內），包含主辦方名稱為 100 字元無斷點字串的情況；主辦方名稱放不下時 SHALL 截斷顯示，且 SHALL 能以提示文字看到完整名稱。

點選選單按鈕 SHALL 開啟標題為「後台選單」的側邊抽屜，抽屜內 SHALL 顯示與水平選單相同的選單項目（項目與顯示規則依「後台導覽入口依頁面權限規則顯示」Requirement），以及「登出」。在抽屜內選取任一選單項目時，系統 SHALL 導覽至該項目的目標路徑（仍經過路由守衛）並關閉抽屜；選取目前所在頁面的項目時同樣關閉抽屜。按 Esc SHALL 關閉抽屜，關閉後焦點 SHALL 回到選單按鈕，不論開啟抽屜時選單按鈕是否曾取得焦點（觸控、滑鼠或鍵盤開啟皆同）。

#### Scenario: AWU-MOBILE-NAV-001 窄螢幕顯示選單按鈕且導覽列不溢出
- **WHEN** 已登入並切換至一個 Approved Organizer、角色為 `Admin` 的使用者，在 W = 320、390、720 時分別開啟 `/admin/redeem` 與 `/admin/venues`，並在主辦方名稱為一般名稱與 100 字元無斷點字串兩種情況下各量測一次
- **THEN** 導覽列不溢出；選單按鈕可見且外框至少 44×44 CSS px；區域切換選單的觸發按鈕可見；水平選單與頂列登出鈕不顯示；`/admin/redeem` 的頁面捲動寬度不超過 W

#### Scenario: AWU-MOBILE-NAV-002 寬螢幕維持水平選單且導覽列不溢出
- **WHEN** 已切換 Organizer、角色為 `Admin` 的使用者在 W = 721、800、1280 時分別開啟 `/admin/redeem` 與 `/admin/venues`，並在主辦方名稱為一般名稱與 100 字元無斷點字串兩種情況下各量測一次
- **THEN** 導覽列不溢出；水平選單、區域切換選單的觸發按鈕與頂列登出鈕可見，選單按鈕不顯示

#### Scenario: AWU-MOBILE-NAV-003 開啟抽屜後顯示選單項目與登出
- **WHEN** 已切換 Organizer 的使用者（角色為 `Admin` 與非 `Admin` 各一次）點選選單按鈕
- **THEN** 開啟標題為「後台選單」的抽屜，選單按鈕的 `aria-expanded` 為 `true`；兩種角色的抽屜內選單項目都依序恰為「場館管理」「活動管理」「訂單管理」「票券核銷」；抽屜內有「登出」

#### Scenario: AWU-MOBILE-NAV-004 在抽屜內選取項目後導覽並關閉抽屜
- **WHEN** 已切換 Organizer、角色非 `Admin` 的使用者在 `/admin/venues` 開啟抽屜並選取「票券核銷」；或在 `/admin/venues` 開啟抽屜並選取「場館管理」（目前所在頁面）
- **THEN** 第一種導覽至 `/admin/redeem`（路由名稱 `admin-redeem`）；第二種停留在 `/admin/venues`；兩者的抽屜都關閉，選單按鈕的 `aria-expanded` 為 `false`

#### Scenario: AWU-MOBILE-NAV-005 按 Esc 關閉抽屜並把焦點還給選單按鈕
- **WHEN** 選單按鈕未取得焦點時，使用者以點擊開啟抽屜後按 Esc
- **THEN** 抽屜關閉，選單按鈕的 `aria-expanded` 為 `false`，目前焦點元素是選單按鈕

#### Scenario: AWU-MOBILE-NAV-006 在抽屜內登出
- **WHEN** 使用者開啟抽屜後點選「登出」
- **THEN** 抽屜在登出流程完成前即關閉（選單按鈕的 `aria-expanded` 為 `false`），系統執行登出並導向 `/login`

#### Scenario: AWU-MOBILE-NAV-007 截斷的主辦方名稱可看到完整名稱
- **WHEN** 已切換 Organizer 的使用者進入後台，目前主辦方名稱為 100 字元字串
- **THEN** 主辦方名稱入口的提示文字（`title` 屬性）為完整的 100 字元名稱

## ADDED Requirements

### Requirement: 三個區域的版面可由區域名稱與背景色辨識，並可由區域切換選單互相前往
前端 SHALL 分為三個區域，各自使用獨立版面：會員前台（`BuyerLayout`，`/` 下的頁面）、主辦方後台（`AdminLayout`，`/admin/*`）、平台管理（`PlatformLayout`，`/platform/*`）。

各區域 SHALL 有不同的頁面背景色：會員前台為 `--color-bg`，主辦方後台為 `--color-bg-organizer`，平台管理為 `--color-bg-platform`；主辦方後台與平台管理的導覽列 SHALL 為白底，頂端有 4px、顏色為該區強調色（`--color-area-organizer`／`--color-area-platform`）的色條。背景色不得是唯一的區分方式：三個版面的導覽列 SHALL 都有區域切換選單（`.area-switcher`），其觸發按鈕以文字顯示目前區域名稱（「會員前台」「主辦方後台」「平台管理」）。

區域切換選單 SHALL 只在已登入時顯示，展開後依序為：
1. 「登入帳號：{目前登入者 Email}」，停用、不可點選；
2. 「會員前台」，前往 `/`；
3. 「主辦方後台」，前往 `/admin`（仍經過路由守衛）；
4. 「平台管理」，前往 `/platform`，僅角色為 `Admin` 時顯示。

目前所在區域的項目 SHALL 停用並加註「（目前）」。Email 與名稱 MUST 以文字插值呈現，不得以 HTML 解析。

#### Scenario: AWU-AREA-SWITCH-001 Admin 在主辦方後台展開區域切換選單
- **WHEN** 角色為 `Admin`、已切換 Organizer 的使用者在主辦方後台點選區域切換選單的觸發按鈕
- **THEN** 觸發按鈕文字含「主辦方後台」；選單項目依序恰為「登入帳號：{Email}」「會員前台」「主辦方後台（目前）」「平台管理」；第一項與「主辦方後台（目前）」為停用狀態

#### Scenario: AWU-AREA-SWITCH-002 非 Admin 看不到平台管理
- **WHEN** 角色非 `Admin` 的已登入使用者在會員前台與主辦方後台分別展開區域切換選單
- **THEN** 選單中不顯示「平台管理」；會員前台的選單項目依序恰為「登入帳號：{Email}」「會員前台（目前）」「主辦方後台」

#### Scenario: AWU-AREA-SWITCH-003 從區域切換選單前往其他區域
- **WHEN** 角色為 `Admin`、尚未切換 Organizer 的使用者在會員前台展開區域切換選單並點選「平台管理」；或在平台管理展開選單並點選「主辦方後台」；或在平台管理展開選單並點選「會員前台」
- **THEN** 第一種最終路徑為 `/platform/organizers`；第二種依 `AWU-GUARD-002` 導向「選擇主辦方」頁面；第三種路徑為 `/`

#### Scenario: AWU-AREA-SWITCH-004 未登入時會員前台不顯示區域切換選單
- **WHEN** 未登入的使用者開啟會員前台首頁
- **THEN** 頁面中沒有 `.area-switcher`

#### Scenario: AWU-AREA-SWITCH-005 Email 含 HTML 標籤時以文字顯示
- **WHEN** 目前登入者的 Email 字串含 `<b>x</b>`，使用者展開區域切換選單
- **THEN** 第一項的文字內容含原字串 `<b>x</b>`，選單內沒有 `b` 元素

#### Scenario: AWU-AREA-LAYOUT-001 三個區域的背景色與色條實際套用
- **WHEN** 在瀏覽器中，已登入且角色為 `Admin`、已切換 Organizer 的使用者分別開啟 `/`、`/admin/venues`、`/platform/organizers`，讀取版面根元素與導覽列的計算樣式
- **THEN** 三個版面根元素的背景色分別等於 `--color-bg`、`--color-bg-organizer`、`--color-bg-platform` 解析出的顏色，三者兩兩不同；主辦方後台與平台管理導覽列的 `border-top` 寬度為 4px、顏色分別等於 `--color-area-organizer`、`--color-area-platform`，背景為白色

### Requirement: 平台管理版面提供審核入口，且 320 至 1280 CSS px 寬時導覽列不溢出
`PlatformLayout` 的導覽列 SHALL 只包含：區域切換選單、水平選單（只有一項「主辦方審核」，前往 `/platform/organizers`）、登出按鈕；不使用抽屜。點選登出 SHALL 執行登出並導向 `/login`。本 Requirement 的「W」與「導覽列不溢出」定義同「後台導覽在窄螢幕收合為抽屜選單，且 320 至 1280 CSS px 寬時不溢出」Requirement（主辦方名稱截斷的例外不適用，平台管理導覽列沒有主辦方名稱）。會員前台頂列加入區域切換選單後同樣 MUST 不溢出。

#### Scenario: AWU-PLATFORM-NAV-001 平台管理導覽列內容
- **WHEN** 角色為 `Admin` 的使用者開啟 `/platform/organizers`
- **THEN** 導覽列有一個 `.area-switcher`（觸發按鈕文字含「平台管理」）、選單項目恰為「主辦方審核」一項、一個「登出」按鈕；點選「登出」後執行登出並導向 `/login`

#### Scenario: AWU-AREA-NAV-OVERFLOW-001 平台管理導覽列與會員前台頂列不溢出
- **WHEN** 在瀏覽器中，角色為 `Admin` 的使用者在 W = 320、390、720、721、800、1280 開啟 `/platform/organizers`；已登入的使用者在 W = 320、390、1280 開啟 `/`
- **THEN** 每次量測中導覽列（平台管理）與頂列（會員前台）不溢出，`.area-switcher` 命中恰好 1 個且可見
