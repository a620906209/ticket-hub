## Purpose

讓 Production 等非本機開發環境在全新資料庫上，能透過部署設定建立首位 Admin 帳號，不必借用 Development 環境的展示用 seeder。

## ADDED Requirements

### Requirement: 資料庫無任何 Admin 時依設定建立首位 Admin
系統 SHALL 於啟動時，在非 Development 環境、已設定 `AdminBootstrap:Email` 與 `AdminBootstrap:Password` 且資料庫中不存在任何角色為 Admin 的會員（不論是否啟用）時，建立一位 Admin 會員，Email 為設定值、密碼以與註冊相同的雜湊方式儲存、帳號為啟用狀態。系統 MUST NOT 建立任何主辦方。

#### Scenario: ADMIN-BOOT-001 無 Admin 時建立
- **WHEN** 資料庫沒有任何 Admin，且兩項設定皆為合法值，系統啟動
- **THEN** 資料庫新增一位角色為 Admin、啟用中的會員，Email 等於設定值，且該會員可用設定的密碼經既有登入流程取得 token
- **AND** 不新增任何 Organizer 或 OrganizerMember

#### Scenario: ADMIN-BOOT-002 已有 Admin 時略過
- **WHEN** 資料庫已存在至少一位 Admin（Email 與設定值不同，或該 Admin 已停用），系統啟動
- **THEN** 系統不新增、不修改任何會員，並記錄一則 Info 日誌

#### Scenario: ADMIN-BOOT-003 不覆寫相同 Email 的既有非 Admin 會員
- **WHEN** 資料庫沒有任何 Admin，但已有一位 Email 與設定值相同的一般會員，系統啟動
- **THEN** 啟動失敗並以明確訊息說明該 Email 已被非 Admin 會員使用（訊息不含 Email 值）；該會員的角色、密碼與狀態維持不變

#### Scenario: ADMIN-BOOT-004 Development 環境不執行
- **WHEN** 環境為 Development，且資料庫沒有任何 Admin、兩項設定皆為合法值
- **THEN** 系統不建立此機制的 Admin（Development 由既有展示用 seeder 負責）

### Requirement: 設定未提供或不合法時的啟動行為
系統 SHALL 在任何環境（含 Development）於啟動時驗證設定：`AdminBootstrap:Email` 與 `AdminBootstrap:Password` 皆未設定（空或空白）時略過建立並記錄 Info；只設定其中一項，或 Email 格式不合法，或密碼不符合註冊的密碼強度規則時，SHALL 使啟動失敗，且錯誤訊息 MUST NOT 包含密碼內容。

#### Scenario: ADMIN-BOOT-005 兩項皆未設定
- **WHEN** 兩項設定皆未提供，系統啟動
- **THEN** 啟動成功、不建立任何會員，並記錄一則說明未設定而略過的 Info 日誌

#### Scenario: ADMIN-BOOT-006 只設定其一
- **WHEN** 只設定 Email 或只設定 Password
- **THEN** 啟動失敗，錯誤訊息指出缺少的設定項名稱

#### Scenario: ADMIN-BOOT-007 值不合法
- **WHEN** Email 不是合法 Email 格式（含前後空白；系統原樣使用 Email，不 Trim 或轉大小寫），或密碼不符合註冊的密碼強度規則（至少 8 碼、含英文字母與數字）
- **THEN** 啟動失敗，錯誤訊息指出不合法的設定項名稱與原因，且不包含密碼內容

### Requirement: 資料庫 schema 未就緒時略過建立
系統 SHALL 在資料庫存在尚未套用的 migration 時略過建立並記錄 Warning，不得使啟動因缺表或缺欄位而失敗。

#### Scenario: ADMIN-BOOT-008 有未套用的 migration
- **WHEN** 設定合法，但資料庫有尚未套用的 migration，系統啟動
- **THEN** 不寫入任何會員，並記錄一則包含未套用 migration 數量的 Warning

### Requirement: 多副本並發啟動時最多建立一位 Admin
系統 SHALL 在多個 api 實例同時啟動且資料庫無 Admin 時，確保以同一 Email 至多建立一位會員；因 Email 唯一性衝突而未能建立者 SHALL 於重新確認資料庫已存在 Admin 後視為成功，不使啟動失敗。

#### Scenario: ADMIN-BOOT-009 並發建立
- **WHEN** 兩個實例同時對無 Admin 的資料庫執行建立
- **THEN** 資料庫中該 Email 恰有一位 Admin 會員，且兩個實例皆正常完成啟動

### Requirement: 建立過程不洩漏機敏資訊
系統 MUST NOT 在任何日誌或錯誤訊息中輸出 `AdminBootstrap:Password` 的值或其雜湊。

#### Scenario: ADMIN-BOOT-010 日誌不含密碼與雜湊
- **WHEN** 建立、略過或失敗等任一路徑執行
- **THEN** 所有輸出的日誌與例外訊息均不包含密碼明文或密碼雜湊
