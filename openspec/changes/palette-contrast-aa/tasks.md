> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **測試種類與 AC 對應**（理由見 design.md 決策 5）。所有 AC 都以 Vitest 單元測試驗證（`?raw` 讀取 CSS，不碰 DB 與網路），不需要 AC 測試對應例外。
>
> | ID | Requirement／Scenario | 驗證方式 | Task |
> | --- | --- | --- | --- |
> | WCC-001 | 實心語意色按鈕三種狀態的白字 | Vitest | 1.2（基準失敗）、2.1、2.4 |
> | WCC-002 | 狀態標籤與提示訊息文字 | Vitest | 1.2、2.1、2.4 |
> | WCC-003 | 連結與主色文字 | Vitest | 1.2、2.1、2.4 |
> | WCC-004 | 一般文字與次要文字 | Vitest | 1.2、2.1、2.4 |
> | WCC-005 | Placeholder | Vitest | 1.2、2.1、2.4 |
> | WCC-006 | error 系列維持為 danger 的別名 | Vitest | 2.1、2.4 |
> | WCC-007 | 輸入框邊框與 hover 邊框 | Vitest | 1.2、2.1、2.4 |
> | WCC-008 | 按鈕焦點框 | Vitest | 1.2、2.1、2.4 |
> | WCC-009 | 調整後的語意色色相與彩度 | Vitest | 2.1、2.4 |
> | WCC-010 | 根元素配色宣告 | Vitest | 1.2、2.3 |
> | WCC-011 | text 按鈕文字對 hover 與 active 背景 | Vitest | 1.2、2.1、2.4 |
> | WCC-012 | 同義 token 同值、裝飾性邊框不變 | Vitest | 1.2、2.2、2.4 |
>
> **不新增整合測試**：本 change 不改後端與 API。

## 1. 測試先行與基準

- [x] 1.1 新增 `web/src/styles/morandi.contrast.test.ts`。測試內的輔助函式：解析 CSS 區塊中的自訂屬性（`:root`、`.el-button`、`.el-button--<語意色>`）、遞迴展開 `var()`（指向不存在的 token 時拋出錯誤，不得回傳空值而讓比較假通過）、WCAG 相對亮度與對比度、sRGB→OKLCH。輔助函式先以參考值驗證：`#000000` 對 `#ffffff` 為 21.00、`#767676` 對 `#ffffff` 為 4.54（±0.01）、同色為 1.00；`#ff0000` 的 OKLCH 色相約 29.2°（±0.5）、明度約 0.628、彩度約 0.258；`#0000ff` 色相約 264.05°、明度約 0.452、彩度約 0.313（strict-reviewer 建議補上，避免 WCC-009 單向彩度斷言在公式偏小時假通過）；`var()` 循環引用或缺少 token 時拋出錯誤
- [x] 1.2 依 spec 為 WCC-001～012 撰寫斷言，每個 `it` 名稱以對應的 WCC 編號開頭（例如 `WCC-001 實心語意色按鈕…`），讓測試結果可回指 AC：
  - WCC-001：從 `.el-button--<語意色>` 區塊讀取 `--el-button-hover-bg-color`、`--el-button-hover-border-color`、`--el-button-active-bg-color`、`--el-button-active-border-color`，該區塊不存在或缺少變數時測試必須失敗；除白字對比外，斷言 hover 相對亮度 < base、active 相對亮度 < hover，以及邊框等於對應背景
  - WCC-008：從 `.el-button` 與 `.el-button--<語意色>` 讀取 `--el-button-outline-color`；另斷言 `morandi.css` 中 `.el-button {` 區塊出現在所有 `.el-button--<語意色>` 區塊之前（兩者權重相同，順序在後者會覆蓋前者；`.el-button` 若排在後面，會把語意色按鈕的焦點框蓋回預設值）
  - WCC-009：原色寫死在測試中，不從 CSS 讀取
  - WCC-011：`--el-fill-color` 在 `morandi.css` 未定義時，以 Element Plus 2.14.4 預設值 `#f0f2f5` 計算（測試內註明來源）
  - WCC-012：`--el-border-color-light`／`lighter` 的原值寫死在測試中

  以**修改前**的 `morandi.css`／`style.css` 執行 `docker compose exec web npm run test -- morandi.contrast`，逐項記錄結果與**失敗原因**（對比不足，或區塊／變數不存在，分開記錄）：WCC-001～005、007、008、010、011 必須失敗（審視報告第 4.1 節已實測不合格；WCC-001、008 另因覆寫區塊不存在而失敗），WCC-006、012 必須通過（現有別名與同值關係正確）；WCC-009 修改前預期通過（新舊值相同，色相差為 0、彩度相等），照實記錄。若預期失敗的項目通過，代表測試有誤，先停下排查

  **修改前基準（2026-10-08）**：47 項中 26 項失敗、21 項通過，符合預期。
  - 對比不足而失敗：WCC-002（五色，例如 warning 2.02）、WCC-003（兩個 token）、WCC-004（`--el-text-color-secondary`、`--color-text-secondary`；其餘三個文字色通過）、WCC-005、WCC-011（primary、danger；`--el-text-color-regular` 通過）
  - 值錯誤：WCC-010（`'light dark'`）
  - 區塊／變數不存在而失敗：WCC-001（五色，`.el-button--<色>` 區塊不存在）、WCC-007（`--el-border-color-hover` 不存在，先於邊框對比被拋出）、WCC-008（`.el-button` 與五個語意色區塊不存在，含順序斷言）
  - 通過：輔助函式 4 項、WCC-006（7 項）、WCC-009（5 項，新舊值相同）、WCC-012
  - **偏差**：Vitest 預設把 CSS 模組（含 `?raw`）換成空字串，design.md 決策 5「沿用 mobile-layout-p0-fixes 的 `?raw` 做法」不能直接套用（那次讀的是 `index.html`）。在 `web/vite.config.ts` 的 `test.css.include` 只放行 `src/styles/morandi.css` 與 `src/style.css`

## 2. 色票修改

- [x] 2.1 依 design.md 決策 1 的方法（OKLCH 只降明度、二分搜尋、兩個條件各留 0.1 餘裕）以容器內的 node 計算確切色票：五色 base、`light-3`／`5`／`7`／`8`／`9`（base 與白色混合）、`dark-2`（混 20% 黑）、active 色（混 35% 黑）；次要文字、placeholder、`--el-border-color`、`--el-border-color-hover` 依決策 3、4 計算。將腳本與輸出記錄在本 task 下方，之後若需重算可重現

  以 `docker compose exec -T web node --input-type=module < palette.mjs` 執行。次要文字、placeholder、邊框沿用同一個「只降明度」搜尋（餘裕同為 0.1）；hover 邊框為邊框混 15% 黑。邊框結果 `#8e877c` 比 design.md 的概估 `#8f897e` 略深一階（概估未統一套 0.1 餘裕），其餘與候選值一致。

  ```js
  const hex2rgb = h => [1,3,5].map(i => parseInt(h.slice(i,i+2),16))
  const rgb2hex = c => '#' + c.map(v => Math.max(0,Math.min(255,Math.round(v))).toString(16).padStart(2,'0')).join('')
  const lin = v => (v/=255) <= 0.04045 ? v/12.92 : ((v+0.055)/1.055)**2.4
  const delin = v => 255*(v <= 0.0031308 ? 12.92*v : 1.055*v**(1/2.4)-0.055)
  const lum = h => { const [r,g,b] = hex2rgb(h).map(lin); return 0.2126*r+0.7152*g+0.0722*b }
  const cr = (a,b) => { const [x,y] = [lum(a),lum(b)].sort((p,q)=>q-p); return (x+0.05)/(y+0.05) }
  function toOklch(h) {
    const [r,g,b] = hex2rgb(h).map(lin)
    const l = Math.cbrt(0.4122214708*r+0.5363325363*g+0.0514459929*b)
    const m = Math.cbrt(0.2119034982*r+0.6806995451*g+0.1073969566*b)
    const s = Math.cbrt(0.0883024619*r+0.2817188376*g+0.6299787005*b)
    const L = 0.2104542553*l+0.793617785*m-0.0040720468*s
    const A = 1.9779984951*l-2.428592205*m+0.4505937099*s
    const B = 0.0259040371*l+0.7827717662*m-0.808675766*s
    let H = Math.atan2(B,A)*180/Math.PI; if (H<0) H+=360
    return { L, C: Math.hypot(A,B), H }
  }
  function fromOklch({L,C,H}) {
    const A = C*Math.cos(H*Math.PI/180), B = C*Math.sin(H*Math.PI/180)
    const l = (L+0.3963377774*A+0.2158037573*B)**3
    const m = (L-0.1055613458*A-0.0638541728*B)**3
    const s = (L-0.0894841775*A-1.291485548*B)**3
    return rgb2hex([
      4.0767416621*l-3.3077115913*m+0.2309699292*s,
      -1.2684380046*l+2.6097574011*m-0.3413193965*s,
      -0.0041960863*l-0.7034186147*m+1.707614701*s].map(delin))
  }
  const mix = (h, target, p) => rgb2hex(hex2rgb(h).map((v,i) => v*(1-p) + hex2rgb(target)[i]*p))
  // 在 OKLCH 只降明度：二分搜尋滿足所有條件的「最淺」明度
  function darkenUntil(orig, ok) {
    const o = toOklch(orig); let lo = 0, hi = o.L
    for (let i = 0; i < 60; i++) { const mid = (lo+hi)/2; ok(fromOklch({...o, L: mid})) ? lo = mid : hi = mid }
    return fromOklch({...o, L: lo})
  }
  const W = '#ffffff', PAGE = '#f3f0ea', M = 0.1
  const originals = { primary:'#8c9a9e', success:'#96a87f', warning:'#c9a66c', danger:'#b97c6d', info:'#a39c93' }
  for (const [name, orig] of Object.entries(originals)) {
    const base = darkenUntil(orig, c => cr(W,c) >= 4.5+M && cr(c, mix(c,W,0.9)) >= 4.5+M)
    const o = toOklch(orig), n = toOklch(base)
    let dh = Math.abs(n.H-o.H)%360; if (dh>180) dh = 360-dh
    const steps = Object.fromEntries([3,5,7,8,9].map(k => [`light-${k}`, mix(base,W,k/10)]))
    const dark2 = mix(base,'#000000',0.2), active = mix(base,'#000000',0.35)
    console.log(name, base, steps, 'dark-2', dark2, 'active', active,
      `| white ${cr(W,base).toFixed(2)} tag ${cr(base,steps['light-9']).toFixed(2)} hover ${cr(W,dark2).toFixed(2)} active ${cr(W,active).toFixed(2)} page ${cr(base,PAGE).toFixed(2)} dH ${dh.toFixed(2)} dC ${(n.C-o.C).toFixed(4)}`)
  }
  const secondary = darkenUntil('#8b8378', c => cr(c,PAGE) >= 4.5+M && cr(c,W) >= 4.5+M)
  const placeholder = darkenUntil('#b3ab9e', c => cr(c,W) >= 4.5+M)
  const border = darkenUntil('#dcd5c9', c => cr(c,W) >= 3+M && cr(c,PAGE) >= 3+M)
  const borderHover = mix(border,'#000000',0.15)
  console.log('secondary', secondary, cr(secondary,PAGE).toFixed(2), cr(secondary,W).toFixed(2))
  console.log('placeholder', placeholder, cr(placeholder,W).toFixed(2))
  console.log('border', border, cr(border,W).toFixed(2), cr(border,PAGE).toFixed(2))
  console.log('border-hover', borderHover, cr(borderHover,W).toFixed(2), cr(borderHover,PAGE).toFixed(2))
  for (const c of ['#f3f0ea','#f0f2f5']) console.log('text-btn bg', c)
  ```

  輸出（節錄）：

  | 色 | base | light-3／5／7／8／9 | dark-2 | active | 白字／標籤／hover／active | 色相差 |
  | --- | --- | --- | --- | --- | --- | --- |
  | primary | `#616f72` | `#909a9c` `#b0b7b9` `#d0d4d5` `#dfe2e3` `#eff1f1` | `#4e595b` | `#3f484a` | 5.22／4.60／7.23／9.39 | 4.69° |
  | success | `#61714b` | `#909c81` `#b0b8a5` `#d0d4c9` `#dfe3db` `#eff1ed` | `#4e5a3c` | `#3f4931` | 5.29／4.65／7.36／9.51 | 0.26° |
  | warning | `#87672d` | `#ab956c` `#c3b396` `#dbd1c0` `#e7e1d5` `#f3f0ea` | `#6c5224` | `#58431d` | 5.24／4.61／7.32／9.39 | 0.47° |
  | danger | `#975d4f` | `#b68e84` `#cbaea7` `#e0ceca` `#eadfdc` `#f5efed` | `#794a3f` | `#623c33` | 5.26／4.62／7.33／9.49 | 0.10° |
  | info | `#726b63` | `#9c9792` `#b9b5b1` `#d5d3d0` `#e3e1e0` `#f1f0ef` | `#5b564f` | `#4a4640` | 5.25／4.61／7.27／9.37 | 3.19° |

  次要文字 `#736b61`（頁面底 4.61、白底 5.24）、placeholder `#7c7468`（4.61）、邊框 `#8e877c`（白底 3.56、頁面底 3.13）、hover 邊框 `#797369`（4.70、4.13）
- [x] 2.2 更新 `web/src/styles/morandi.css`：`:root` 的語意色各階、文字色（`--color-text-secondary` 與 `--el-text-color-secondary` 同值）、placeholder、`--color-primary`（與新的 `--el-color-primary` 同值）、`--el-border-color`，新增 `--el-border-color-hover`；新增 `.el-button` 焦點框覆寫與五個 `.el-button--<語意色>` 的 hover 背景與邊框、active 背景與邊框、焦點框覆寫（決策 2），`.el-button` 區塊須排在五個語意色區塊之前，各附一行說明「為什麼」的註解。error 別名區塊、`--el-border-color-light`／`lighter` 不動
- [x] 2.3 `web/src/style.css` 的 `color-scheme: light dark` 改為 `color-scheme: light`
- [x] 2.4 執行 `docker compose exec web npm run test -- morandi.contrast`，WCC-001～012 全部通過；再執行完整前端測試 `docker compose exec web npm run test` 與 `docker compose exec web npm run build`（含 `vue-tsc -b`），皆須通過。若有既有測試失敗，先在 HEAD 上重跑確認是否為既有 flake，並照實記錄

  **結果（2026-10-08）**：`morandi.contrast` 47/47 通過；完整前端測試 37 檔／488 項通過；`npm run build` 通過（只有既有的 chunk > 500 kB 警告）

## 3. 瀏覽器驗證

- [x] 3.1 修改前（2.2 之前）在瀏覽器以 javascript 讀取一個實心 primary 按鈕的 `getComputedStyle(button).getPropertyValue('--el-button-hover-bg-color')` 與 `--el-button-outline-color`、一個 `el-input__wrapper` 所在元素的 `--el-border-color-hover`，記錄修改前的值（預期為舊的 `light-3`、`light-5` 與 Element Plus 預設冷灰），並截圖買家活動列表、活動詳情頁、登入頁、後台活動列表作為基準

  **進度（2026-10-08）**：computed style 基準已記錄（登入頁）：hover `#a9b4b7`（舊 light-3）、outline `#c0c8ca`（舊 light-5）、input hover 邊框 `#c0c4cc`（Element Plus 冷灰），與預期一致。買家活動列表、活動詳情、登入頁的修改前截圖已擷取。後台活動列表於使用者登入後，以 `git stash push -- web/src/styles/morandi.css web/src/style.css` 暫時還原舊色票補截（截圖時讀到 `--el-color-primary` 為 `#8c9a9e`，確認是舊值），截完立即 `git stash pop`
- [x] 3.2 修改後重讀 3.1 的同一組 computed style，值必須等於 2.1 算出的 `dark-2`、base 與 hover 邊框色，證明 `morandi.css` 的覆寫沒有被 Element Plus 的規則蓋掉；另讀一個 `type="error"` 的 `el-alert` 與一個 `el-tag` 的 computed `color` 與 `background-color`，必須等於新的 base 與 `light-9`。任一不符先停下排查

  **結果（2026-10-08）**：primary 按鈕 hover `#4e595b`（dark-2）、active `#3f484a`、outline `#616f72`（base）；input 邊框 `#8e877c`、hover 邊框 `#797369`；`el-alert--error is-light`（在登入頁動態建立的探針元素）color `#975d4f`、背景 `#f5efed`；活動列表 `el-tag` info `#726b63`／`#f1f0ef`、success `#61714b`／`#eff1ed`；根元素 `color-scheme: light`。另以 `:focus-visible` 確認 primary 按鈕焦點框為 `#616f72` 2px solid。全部與 2.1 一致
- [x] 3.3 修改後截圖 3.1 的同一組頁面，逐頁目視確認：沒有文字消失或對比反轉、hover 時按鈕變深、Tab 焦點框清楚可見、輸入框邊框可辨識。將修改前後的截圖交給使用者確認視覺方向；後台頁面需要登入，若使用者尚未登入，該頁的截圖延到使用者登入後進行，在此之前本 task 不得勾選

  **結果（2026-10-08）**：買家活動列表、活動詳情、登入頁、後台活動列表修改前後截圖已交使用者，使用者確認視覺方向（2026-10-08）。目視：無文字消失或對比反轉、主色按鈕與標籤變深、輸入框邊框可辨識；後台票房進度條隨 success／danger 調暗而明顯變重。hover 與 Tab 焦點框因瀏覽器分頁處於 hidden 無法截圖，改以 computed style 確認（hover `#4e595b`、焦點框 `#616f72` 2px solid）

## 4. 文件與收尾

- [x] 4.1 更新 `docs/ui-ux-review-2026-10-08.md` 第 4.1 節：標註已由 `palette-contrast-aa` 修正，附上新值的對比度；第 2.2 節的 `color-scheme` 項目標註已修正
- [x] 4.2 呼叫 `strict-reviewer` 審查（本 change 只改前端樣式與測試，不涉及 Application／Repository 層，不適用 hardener）；依結果修正後重跑 2.4

  **結果（2026-10-08）**：PASS，0 個 blocking。採納 2 項建議：OKLCH 輔助函式補上明度、彩度參考值與第二個色相點（#0000ff）；morandi.css 檔頭移除歸檔後會失效的 tasks.md 路徑。未採納：active 色未斷言等於「base 混 35% 黑」（spec 未要求，對比度與「比 hover 深」的斷言仍會把關）。重跑 2.4：37 檔／488 項通過，build 通過
