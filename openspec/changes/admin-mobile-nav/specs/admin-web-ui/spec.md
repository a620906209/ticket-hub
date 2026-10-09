## ADDED Requirements

### Requirement: 後台導覽在窄螢幕收合為抽屜選單，且 320 至 1280 CSS px 寬時不溢出
後台導覽列 SHALL 依視窗寬度切換版面，且在 320 ≤ W ≤ 1280 時 MUST 不溢出。本 Requirement 中的「W」指瀏覽器視窗的版面寬度（CSS px，不含捲軸）；版面切換以 CSS media query 判定，其寬度含傳統捲軸，因此有傳統捲軸的桌面瀏覽器在 W 約 706～720 時可能已顯示寬螢幕版面，這個區間的版面切換位置不在本需求的保證範圍內，但仍須不溢出；「導覽列不溢出」指導覽列內每個元素的右緣都不超過 W，且除了以截斷方式顯示的主辦方名稱以外，沒有元素的內容寬度超過自身外框寬度。

W ≤ 720 時，後台導覽列 SHALL 只顯示選單按鈕與目前主辦方名稱入口，水平選單 MUST NOT 顯示；選單按鈕的觸控區 MUST 至少 44×44 CSS px，並提供無障礙名稱「開啟後台選單」與表示抽屜開關狀態的 `aria-expanded`。W > 720 時，系統 SHALL 顯示水平選單，選單按鈕 MUST NOT 顯示。320 ≤ W ≤ 1280 時，導覽列 MUST 不溢出（W < 320 與 W > 1280 不在本需求的保證與驗收範圍內），包含主辦方名稱為 100 字元無斷點字串的情況；主辦方名稱放不下時 SHALL 截斷顯示，且 SHALL 能以提示文字看到完整名稱。

點選選單按鈕 SHALL 開啟標題為「後台選單」的側邊抽屜，抽屜內 SHALL 顯示與水平選單相同的選單項目（項目與顯示規則依「後台導覽入口依頁面權限規則顯示」Requirement），以及「登出」。在抽屜內選取任一選單項目時，系統 SHALL 導覽至該項目的目標路徑（仍經過路由守衛）並關閉抽屜；選取目前所在頁面的項目時同樣關閉抽屜。按 Esc SHALL 關閉抽屜，關閉後焦點 SHALL 回到選單按鈕，不論開啟抽屜時選單按鈕是否曾取得焦點（觸控、滑鼠或鍵盤開啟皆同）。

#### Scenario: AWU-MOBILE-NAV-001 窄螢幕顯示選單按鈕且導覽列不溢出
- **WHEN** 已登入並切換至一個 Approved Organizer、角色為 `Admin` 的使用者，在 W = 320、390、720 時分別開啟 `/admin/redeem` 與 `/admin/venues`，並在主辦方名稱為一般名稱與 100 字元無斷點字串兩種情況下各量測一次；另以尚未切換 Organizer 的 `Admin` 在 W = 320、390 時開啟 `/admin/organizers` 量測一次
- **THEN** 導覽列不溢出；選單按鈕可見且外框至少 44×44 CSS px；水平選單與頂列登出鈕不顯示；`/admin/redeem` 的頁面捲動寬度不超過 W

#### Scenario: AWU-MOBILE-NAV-002 寬螢幕維持水平選單且導覽列不溢出
- **WHEN** 已切換 Organizer、角色為 `Admin` 的使用者在 W = 721、800、1280 時分別開啟 `/admin/redeem` 與 `/admin/venues`，並在主辦方名稱為一般名稱與 100 字元無斷點字串兩種情況下各量測一次
- **THEN** 導覽列不溢出；水平選單與頂列登出鈕可見，選單按鈕不顯示

#### Scenario: AWU-MOBILE-NAV-003 開啟抽屜後依角色顯示選單項目與登出
- **WHEN** 已切換 Organizer 的使用者點選選單按鈕
- **THEN** 開啟標題為「後台選單」的抽屜，選單按鈕的 `aria-expanded` 為 `true`；角色非 `Admin` 時抽屜內選單項目依序恰為「場館管理」「活動管理」「訂單管理」「票券核銷」，角色為 `Admin` 時另有「主辦方審核」；抽屜內有「登出」

#### Scenario: AWU-MOBILE-NAV-004 在抽屜內選取項目後導覽並關閉抽屜
- **WHEN** 已切換 Organizer、角色非 `Admin` 的使用者在 `/admin/venues` 開啟抽屜並選取「票券核銷」；或在 `/admin/venues` 開啟抽屜並選取「場館管理」（目前所在頁面）；或尚未切換 Organizer 的 `Admin` 在 `/admin/organizers` 開啟抽屜並選取「場館管理」
- **THEN** 第一種導覽至 `/admin/redeem`（路由名稱 `admin-redeem`）；第二種停留在 `/admin/venues`；第三種依 `AWU-GUARD-002` 被導向「選擇主辦方」頁面；三者的抽屜都關閉，選單按鈕的 `aria-expanded` 為 `false`

#### Scenario: AWU-MOBILE-NAV-005 按 Esc 關閉抽屜並把焦點還給選單按鈕
- **WHEN** 選單按鈕未取得焦點時，使用者以點擊開啟抽屜後按 Esc
- **THEN** 抽屜關閉，選單按鈕的 `aria-expanded` 為 `false`，目前焦點元素是選單按鈕

#### Scenario: AWU-MOBILE-NAV-006 在抽屜內登出
- **WHEN** 使用者開啟抽屜後點選「登出」
- **THEN** 抽屜在登出流程完成前即關閉（選單按鈕的 `aria-expanded` 為 `false`），系統執行登出並導向 `/login`

#### Scenario: AWU-MOBILE-NAV-007 截斷的主辦方名稱可看到完整名稱
- **WHEN** 已切換 Organizer 的使用者進入後台，目前主辦方名稱為 100 字元字串
- **THEN** 主辦方名稱入口的提示文字（`title` 屬性）為完整的 100 字元名稱
