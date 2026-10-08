# web-color-contrast Specification

## Purpose
定義前端共用色票（`web/src/styles/morandi.css`，買家端與後台共用）的最低對比度要求：文字與語意色元件符合 WCAG AA、輸入類元件邊界與按鈕焦點框不低於 3:1，並限制調色時須保留莫蘭迪色相與低彩度。由 `web/src/styles/morandi.contrast.test.ts` 直接解析色票驗證。來源 change：`openspec/changes/archive/2026-10-08-palette-contrast-aa/`。

## Requirements
### Requirement: 文字與語意色元件的對比度符合 WCAG AA
前端色票（`web/src/styles/morandi.css`，買家端與後台共用）定義的文字色與語意色，在下列組合中的對比度 MUST 不低於 4.5:1（WCAG 2.1 相對亮度公式）。「白底」指 `--el-bg-color`（`#ffffff`），「頁面底」指 `--el-bg-color-page` 與 `--color-bg`。語意色指 primary、success、warning、danger、info 五色；`--el-color-error` 系列 SHALL 維持為 danger 系列的別名。

- 實心語意色按鈕：白字對該色按鈕的靜態背景（base）、hover 背景、active 背景；hover SHALL 比 base 深，active SHALL 比 hover 深。
- text 按鈕：文字色對 hover 背景與 active 背景。
- 狀態標籤與提示訊息（`el-tag`、`el-alert`、`ElMessage` 的淺色樣式）：base 色文字對該色的 `light-9` 背景。
- 連結與主色文字（`--color-primary`、`--el-color-primary`）：對白底與頁面底。
- 文字色（`--el-text-color-primary`、`--el-text-color-regular`、`--el-text-color-secondary`、`--color-text`、`--color-text-secondary`）：對白底與頁面底。
- Placeholder（`--el-text-color-placeholder`）：對白底（輸入框背景）。

停用狀態（disabled）的元件不在此限（WCAG 1.4.3 例外）。`--color-primary` SHALL 等於 `--el-color-primary`，`--color-text-secondary` SHALL 等於 `--el-text-color-secondary`；表格與分隔線的 `--el-border-color-light`／`--el-border-color-lighter` SHALL 維持原值。

#### Scenario: WCC-001 實心語意色按鈕三種狀態的白字
- **WHEN** 讀取色票中五個語意色的 base 值，以及 `.el-button--<語意色>` 覆寫的 hover 背景、hover 邊框、active 背景、active 邊框
- **THEN** 白色（`#ffffff`）對 base、hover 背景、active 背景的對比度都不低於 4.5；每一色的 hover 背景相對亮度低於 base、active 背景相對亮度低於 hover 背景（hover 變深、active 更深）；hover 邊框等於 hover 背景，active 邊框等於 active 背景

#### Scenario: WCC-002 狀態標籤與提示訊息文字
- **WHEN** 讀取五個語意色的 base 值與對應的 `light-9` 值
- **THEN** base 對 `light-9` 的對比度都不低於 4.5

#### Scenario: WCC-003 連結與主色文字
- **WHEN** 讀取 `--color-primary` 與 `--el-color-primary`
- **THEN** 兩者對白底與頁面底的對比度都不低於 4.5

#### Scenario: WCC-011 text 按鈕文字對 hover 與 active 背景
- **WHEN** 讀取 text 按鈕使用的文字色（primary 與 danger 的 base、預設按鈕的 `--el-text-color-regular`），以及 text 按鈕的 hover 背景 `--el-fill-color-light` 與 active 背景 `--el-fill-color`（`morandi.css` 未覆寫時，以 Element Plus 2.14.4 的預設值 `#f0f2f5` 計算）
- **THEN** 每一個文字色對兩個背景的對比度都不低於 4.5

#### Scenario: WCC-012 同義 token 同值、裝飾性邊框不變
- **WHEN** 讀取 `--color-primary`、`--el-color-primary`、`--color-text-secondary`、`--el-text-color-secondary`、`--el-border-color-light`、`--el-border-color-lighter`
- **THEN** `--color-primary` 等於 `--el-color-primary`，`--color-text-secondary` 等於 `--el-text-color-secondary`，`--el-border-color-light` 仍為 `#e5dfd5`、`--el-border-color-lighter` 仍為 `#ede8df`

#### Scenario: WCC-004 一般文字與次要文字
- **WHEN** 讀取五個文字色 token
- **THEN** 每一個對白底與頁面底的對比度都不低於 4.5

#### Scenario: WCC-005 Placeholder
- **WHEN** 讀取 `--el-text-color-placeholder`
- **THEN** 對白底的對比度不低於 4.5

#### Scenario: WCC-006 error 系列維持為 danger 的別名
- **WHEN** 解析 `--el-color-error`、`--el-color-error-light-3`／`-5`／`-7`／`-8`／`-9`、`--el-color-error-dark-2`
- **THEN** 每一個解析後的值都等於對應的 danger token

### Requirement: 輸入類元件邊界與按鈕焦點框的對比度不低於 3:1
輸入類元件（輸入框、選單、日期選擇器、數字輸入）的邊框色 `--el-border-color`，與其 hover 邊框色 `--el-border-color-hover`，對白底與頁面底的對比度 MUST 不低於 3:1（WCAG 1.4.11）；hover 邊框 SHALL 比一般邊框深（相對亮度較低），不得在 hover 時變淡。按鈕的鍵盤焦點框色（預設按鈕 `.el-button` 與五個語意色按鈕的 `--el-button-outline-color`）對白底與頁面底的對比度 MUST 不低於 3:1。表格與分隔線使用的 `--el-border-color-light`／`--el-border-color-lighter` 屬於裝飾性邊界，不在此限。

#### Scenario: WCC-007 輸入框邊框與 hover 邊框
- **WHEN** 讀取 `--el-border-color` 與 `--el-border-color-hover`
- **THEN** 兩者對白底與頁面底的對比度都不低於 3.0，且 hover 邊框的相對亮度低於一般邊框

#### Scenario: WCC-008 按鈕焦點框
- **WHEN** 讀取 `.el-button` 與五個 `.el-button--<語意色>` 解析後的 `--el-button-outline-color`
- **THEN** 每一個對白底與頁面底的對比度都不低於 3.0

### Requirement: 語意色保留莫蘭迪色相與低彩度
為了維持品牌的莫蘭迪色系，五個語意色調整後的 base 值，與調整前的原色（primary `#8c9a9e`、success `#96a87f`、warning `#c9a66c`、danger `#b97c6d`、info `#a39c93`）相比，OKLCH 色相差 MUST 不超過 5°，OKLCH 彩度 MUST NOT 高於原色彩度加 0.01。

#### Scenario: WCC-009 調整後的語意色色相與彩度
- **WHEN** 將五個語意色的 base 值轉換為 OKLCH，與上列原色比較
- **THEN** 每一色的色相差都不超過 5°，彩度都不高於原色彩度加 0.01

### Requirement: 文件只宣告淺色配色
前端全域樣式 SHALL 將根元素的 `color-scheme` 宣告為 `light`，不得包含 `dark`，避免作業系統深色模式下原生捲軸與表單元件變成深色、與沒有深色 token 的頁面本體不一致。

#### Scenario: WCC-010 根元素配色宣告
- **WHEN** 讀取 `web/src/style.css` 中 `:root` 的 `color-scheme` 宣告
- **THEN** 值為 `light`

