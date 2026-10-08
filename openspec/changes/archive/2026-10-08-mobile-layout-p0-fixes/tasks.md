> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **測試種類與 AC 對應**（理由見 design.md 決策 3）。
>
> **AC 測試對應例外**：BW-MOBILE-001～005 以瀏覽器量測取代 CLAUDE.md 要求的單元或整合測試，例外紀錄見 design.md「AC 測試對應例外」。核准狀態：已核准（2026-10-08）。
>
> | ID | Scenario | 驗證方式 | Task |
> | --- | --- | --- | --- |
> | BW-MOBILE-001 | 390px 寬未登入瀏覽活動詳情頁 | 瀏覽器量測腳本 | 1.2（基準值）、3.2、3.7 |
> | BW-MOBILE-002 | 320px 寬未登入瀏覽活動詳情頁 | 瀏覽器量測腳本 | 1.2（基準值）、3.3、3.7 |
> | BW-MOBILE-003 | 已登入買家在窄螢幕瀏覽含計數票種的活動詳情頁（390／320） | 瀏覽器量測腳本（使用者手動登入） | 3.4、3.7 |
> | BW-MOBILE-004 | 桌面與平板寬度維持雙欄版面（721／800／1280） | 瀏覽器量測腳本 | 1.2（基準值）、3.5 |
> | BW-MOBILE-005 | 斷點上限寬度維持單欄且不橫向捲動（720） | 瀏覽器量測腳本 | 1.2（基準值）、3.6 |
> | BW-LANG-001 | 根元素語言宣告 | Vitest 單元測試（`?raw` 讀檔，不碰 DB 與網路） | 2.1 |
>
> **不新增整合測試**：本 change 不改後端與 API。

## 1. 修改前基準量測

- [x] 1.1 選定測試活動並記錄其 id：兩者都必須是**非排隊（非熱門搶購）模式**，否則 `.quick-pick` 不會渲染。未登入量測用「含座位制票種」的活動；BW-MOBILE-003 用「同時有座位制與計數票種、且販售中」的活動（可與前者相同）。此外，兩者都必須**實際已產生座位資料**（座位區塊由座位資料分組渲染，見 `EventDetailPage.vue` 的 `seatsByZone`；只有座位制票種、沒有座位資料時不會出現 `.zone-block` 與 `.seat-btn`）。BW-MOBILE-003 的活動也必須至少有一個計數票種。選定後，先以該活動 id 跑一次量測腳本（BW-MOBILE-003 的活動須在登入後以 `REQUIRE_COUNT = true` 執行；若此時使用者尚未登入，這項確認延到 3.4 開始量測前進行，1.1 仍可先勾選未登入的部分），確認 `missing` 為空，證明測試資料符合前置條件。若沒有符合條件的活動，先回報，再決定測試資料，不得跳過。之後所有量測都使用同一組 id

  > **紀錄（2026-10-08）**：選定活動 `a1a3e2b4-b132-41cb-90ee-622f8d2d02e2`（實測演唱會；非排隊、座位制與計數票種各 1、已產生 2 個座位、未設販售期間），未登入與 BW-MOBILE-003 共用。未登入量測 `missing` 為空（見 1.2 紀錄）；`REQUIRE_COUNT = true` 的確認延到 3.4。
- [x] 1.2 在目前 Vite dev server 的 origin（`web/certs` 有憑證時為 `https://localhost:5173`，否則為 `http://localhost:5173`，見 `web/vite.config.ts`）的任一頁，以 javascript 執行下方量測腳本，W 分別為 320、390、720、721、800、1280，記錄每次的回傳值。**預期 390 與 320 會重現 `scrollWidth` > W（報告實測 390 時為 470）**；721 依推算（購票欄約 337px，小於 `.quick-pick` 約 452px 的最小寬度）也可能溢出，照實記錄即可，不作為停止條件。若 390 或 320 沒有重現，代表腳本或測試資料有誤，先停下排查，不得進入第 3 節。若使用者此時已登入，也對 BW-MOBILE-003 的活動以 W = 320 記錄一次修改前的 `outOfViewport`（`REQUIRE_COUNT = true`），作為量測能抓到計數列溢出的證據；未登入時則註記「無已登入基準」

  量測腳本（`EVENT_ID`、`W`、`REQUIRE_COUNT`、`INJECT_LONG_TEXT`、`FORCE_NO_OVERFLOW_WRAP` 依情況替換；`REQUIRE_COUNT` 只有 3.4 已登入量測時為 `true`；`INJECT_LONG_TEXT` 只有 3.7 為 `true`；`FORCE_NO_OVERFLOW_WRAP` 只在 3.7 重現修改前的失敗時為 `true`）：

  ```js
  (async (EVENT_ID, W, REQUIRE_COUNT, INJECT_LONG_TEXT, FORCE_NO_OVERFLOW_WRAP) => {
    const frame = document.createElement('iframe');
    frame.style.cssText = `position:fixed;top:0;left:0;width:${W}px;height:10000px;border:0;z-index:99999;background:#fff`;
    frame.src = `/events/${EVENT_ID}`;
    document.body.appendChild(frame);
    await new Promise((resolve) => frame.addEventListener('load', resolve, { once: true }));
    await new Promise((resolve) => setTimeout(resolve, 2500));
    const doc = frame.contentDocument;
    // 量測基準一律是 W（與 spec 的視窗寬度一致）。頁面永遠比視窗高（header 1px 邊框加上頁面 margin-top 32px 摺疊到 main 外），加高 iframe 也無法消除垂直捲軸，
    // 因此隱藏 iframe 的捲軸（仍可捲動），模擬手機覆蓋式捲軸不佔寬度的情境；
    // 若捲軸仍佔寬度（clientWidth < W），0～15px 的溢出會被捲軸吸收，因此列為 missing，使該次量測無效
    doc.documentElement.style.scrollbarWidth = 'none';
    // 只用來重現修改前的失敗：強制關閉 overflow-wrap，證明這個量測在沒有修正時會失敗
    if (FORCE_NO_OVERFLOW_WRAP) {
      const style = doc.createElement('style');
      style.textContent = '.layout { overflow-wrap: normal !important; }';
      doc.head.appendChild(style);
    }
    // 主辦方輸入的標題、描述、分區名稱可能是無斷點的長字串（網址、英數代碼，zoneCode 上限 50 字元）；
    // 測試資料不含這類內容，因此以 DOM 注入模擬。描述段落複製 h1 的 scoped 屬性，才會套用頁面的 .description 樣式
    const longUrl = 'https://www.facebook.com/events/1234567890123456789/';
    if (INJECT_LONG_TEXT) {
      const title = doc.querySelector('.info-column h1');
      title.textContent = longUrl;
      const description = doc.createElement('p');
      [...title.attributes].filter((attribute) => attribute.name.startsWith('data-v-')).forEach((attribute) => description.setAttribute(attribute.name, ''));
      description.className = 'description';
      description.textContent = `活動連結：${longUrl}`;
      title.after(description);
      doc.querySelectorAll('.count-ticket-name').forEach((el) => { el.textContent = 'X'.repeat(50); });
      doc.querySelectorAll('.zone-block h3').forEach((el) => { el.textContent = `${'Y'.repeat(50)} 區`; });
    }
    // 改完捲軸與注入內容後等 el-table 的 ResizeObserver 重算寬度；背景分頁不會觸發 requestAnimationFrame，所以用 setTimeout
    await new Promise((resolve) => setTimeout(resolve, 300));
    const clientWidth = doc.documentElement.clientWidth;
    const viewportMismatch = clientWidth === W ? [] : [`clientWidth=${clientWidth} ≠ W（捲軸仍佔寬度，量測無效）`];
    const rectOf = (selector) => doc.querySelector(selector)?.getBoundingClientRect() ?? null;
    const isVisibleRect = (rect) => rect !== null && rect.width > 0 && rect.height > 0;
    // 前置條件：這些元素必須存在且有非零尺寸，否則下方的比較可能因空集合或 -1 而假通過
    const requiredSelectors = [
      '.layout', '.info-column', '.info-column h1', '.info-column .start-at', '.info-column .sales-window',
      '.info-column .el-table', '.purchase-column', '.quick-pick', '.quick-pick .quick-pick-label', '.quick-pick .el-select', '.quick-pick .el-input-number', '.quick-pick .el-button',
      '.zone-block', '.zone-block .seat-btn', '.summary', '.summary p', '.summary .el-button',
      ...(REQUIRE_COUNT ? ['.count-ticket-row'] : []),
    ];
    const missing = [...viewportMismatch, ...requiredSelectors.filter((selector) => !isVisibleRect(rectOf(selector)))];
    // 注入後等待期間 Vue 可能重繪而還原內容，描述段落也可能沒套到 scoped 樣式；任一情況都會讓長字串檢查假通過，因此列為 missing
    if (INJECT_LONG_TEXT) {
      const injectedDescription = doc.querySelector('.info-column .description');
      if (doc.querySelector('.info-column h1')?.textContent !== longUrl) missing.push('注入的標題已被還原');
      if (!injectedDescription || getComputedStyle(injectedDescription).whiteSpace !== 'pre-wrap') missing.push('注入的描述未套用 .description 樣式');
      if ([...doc.querySelectorAll('.count-ticket-name')].some((el) => el.textContent !== 'X'.repeat(50))) missing.push('注入的計數票種名稱已被還原');
      if ([...doc.querySelectorAll('.zone-block h3')].some((el) => !el.textContent.startsWith('Y'.repeat(50)))) missing.push('注入的分區名稱已被還原');
    }
    // 計數購票列逐列確認四個項目（區域、價格、可售數量、張數輸入）都存在且可見，只確認容器不足以證明內容完整顯示
    if (REQUIRE_COUNT) {
      const countRowParts = ['.count-ticket-name', '.count-ticket-price', '.count-ticket-quantity', '.el-input-number'];
      doc.querySelectorAll('.count-ticket-row').forEach((row, index) => {
        countRowParts
          .filter((part) => !isVisibleRect(row.querySelector(part)?.getBoundingClientRect() ?? null))
          .forEach((part) => missing.push(`.count-ticket-row[${index}] ${part}`));
      });
    }
    // block 元素的外框寬度永遠等於欄寬，文字溢出時外框不變，只看 rect.right 會假通過；因此同時以 left + scrollWidth 取內容右緣
    const contentRightOf = (el) => {
      const rect = el.getBoundingClientRect();
      return Math.round(Math.max(rect.right, rect.left + el.scrollWidth));
    };
    // 版面內每個可見元素的外框與內容都不得超出視窗右緣，涵蓋規格要求完整顯示的所有內容（含時間、販售期間、座位區塊）
    const outOfViewport = [...doc.querySelectorAll('.layout *')]
      .filter((el) => isVisibleRect(el.getBoundingClientRect()))
      .map((el) => ({ el, right: Math.round(el.getBoundingClientRect().right), hasContentOverflow: el.clientWidth > 0 && el.scrollWidth > el.clientWidth + 1 }))
      .filter(({ right, hasContentOverflow }) => right > W || hasContentOverflow)
      .slice(0, 10)
      .map(({ el, right, hasContentOverflow }) => `${el.tagName.toLowerCase()}.${[...el.classList].join('.')} right=${right}${hasContentOverflow ? ` scrollWidth=${el.scrollWidth}>clientWidth=${el.clientWidth}` : ''}`);
    const infoColumn = rectOf('.info-column');
    const purchaseColumn = rectOf('.purchase-column');
    const layout = doc.querySelector('.layout');
    const scrollWidth = doc.documentElement.scrollWidth;
    const result = {
      W,
      clientWidth,
      missing,
      outOfViewport,
      scrollWidth,
      // computed 值是以空白分隔的像素值（例如 '320px 400px'），拆成陣列後以長度判斷欄數、以 [0] 判斷第一欄
      gridColumns: layout ? getComputedStyle(layout).gridTemplateColumns.trim().split(/\s+/) : [],
      isLayoutPass: missing.length === 0 && outOfViewport.length === 0 && scrollWidth <= W,
      // Scenario 指名的元素逐一輸出內容右緣，供 task 直接追溯（每個陣列都必須非空且每個值 ≤ W）
      namedRights: Object.fromEntries(
        [
          ['title', '.info-column h1'],
          ['priceTable', '.info-column .el-table'],
          ['countTicketRows', '.count-ticket-row'],
          ['summary', '.summary'],
        ].map(([name, selector]) => [name, [...doc.querySelectorAll(selector)].map(contentRightOf)]),
      ),
      infoColumnWidth: Math.round(infoColumn?.width ?? NaN),
      infoColumnRight: Math.round(infoColumn?.right ?? NaN),
      infoColumnBottom: Math.round(infoColumn?.bottom ?? NaN),
      purchaseColumnLeft: Math.round(purchaseColumn?.left ?? NaN),
      purchaseColumnTop: Math.round(purchaseColumn?.top ?? NaN),
      quickPickSelectTop: Math.round(rectOf('.quick-pick .el-select')?.top ?? NaN),
      quickPickButtonTop: Math.round(rectOf('.quick-pick .el-button')?.top ?? NaN),
    };
    frame.remove();
    return result;
  })('EVENT_ID', W, false, false, false);
  ```

  > **每次量測的有效條件**：`missing` 必須是空陣列。若不是，代表頁面尚未載入完成、測試活動不符條件（例如排隊模式不會渲染 `.quick-pick`），或已登入量測時登入狀態尚未還原，**該次量測無效，不得用來判定通過或失敗**。登入狀態要經過 refresh 還原，會多一次網路往返，此時把等待時間從 2500 延長到 5000 再量一次。缺少元素時，相關數值欄位為 `NaN`，與任何數字比較都不成立；不過判定一律以 `missing` 為準，不依賴這個性質。
  >
  > 以下「版面通過」指腳本回傳 `isLayoutPass === true`，即 `missing` 為空、`outOfViewport` 為空，且 `scrollWidth` ≤ W。`missing` 也包含「`clientWidth` 必須等於 W」的檢查，確保量測基準與 spec 的視窗寬度一致（見腳本註解）。

  > **1.2 基準紀錄（2026-10-08，修改前，未登入，無已登入基準）**：首次執行時頁面永遠比視窗高約 33px（header 實高 57px，比 `BuyerLayout.vue` 的 `calc(100svh - 56px)` 多 1px 邊框；另外 32px 是 `.event-detail-page` 的 `margin-top` 摺疊到 `main` 外），`clientWidth` 恆為 W − 15，量測全數無效；經使用者同意，腳本改為以 `scrollbar-width: none` 隱藏 iframe 捲軸後重量（design.md 決策 3 已同步）。所有寬度 `clientWidth === W`、`missing` 為空。
  >
  > | W | scrollWidth | gridColumns | isLayoutPass | 備註 |
  > | --- | --- | --- | --- | --- |
  > | 320 | 470 | `454px` | false | 重現溢出；title／priceTable／countTicketRows／summary 右緣皆 470 |
  > | 390 | 470 | `454px` | false | 重現溢出（與報告的 470 一致） |
  > | 720 | 720 | `688px` | true | |
  > | 721 | 822 | `320px 454px` | false | 購票欄被 `.quick-pick` 撐到 454px |
  > | 800 | 822 | `320px 454px` | false | 同上 |
  > | 1280 | 1280 | `320px 728px` | true | infoColumnWidth 320，purchaseColumnLeft 452 > infoColumnRight 420 |

## 2. 語言宣告（BW-LANG-001）

- [x] 2.1 新增 `web/src/indexHtml.test.ts`：以 `import indexHtml from '../index.html?raw'` 取得內容，用 `new DOMParser().parseFromString(indexHtml, 'text/html')` 解析，斷言 `documentElement.lang` 為 `zh-Hant-TW`，測試名稱前綴 `[BW-LANG-001]`。**不得使用 `node:fs`**（理由見 design.md 決策 3）。先執行並確認**失敗**（目前是 `en`）
- [x] 2.2 `web/index.html`：`lang="en"` 改為 `lang="zh-Hant-TW"`，重跑 2.1 確認通過

## 3. 活動詳情頁版面（BW-MOBILE-001～005）

- [x] 3.1 修改 `web/src/pages/buyer/EventDetailPage.vue` 的 `<style scoped>`（design.md 決策 1），不修改 template 與 script：
  - `.layout` 改為 `grid-template-columns: 320px minmax(0, 1fr)`
  - `@media (max-width: 720px)` 內改為 `minmax(0, 1fr)`
  - `.quick-pick` 加上 `flex-wrap: wrap`
  - `.count-ticket-row` 加上 `flex-wrap: wrap`
  - `.layout` 加上 `overflow-wrap: anywhere`（第二輪審查追加，見 design.md 決策 1）
- [x] 3.2 [BW-MOBILE-001] 以 W = 390 重跑量測腳本：版面通過，且 `namedRights.title` 與 `namedRights.priceTable` 皆非空、每個值 ≤ 390（Scenario 指定的標題與票價表右緣）。記錄數值
- [x] 3.3 [BW-MOBILE-002] 以 W = 320 重跑：版面通過，且 `quickPickButtonTop` > `quickPickSelectTop`（按鈕換到下一行；以此幾何判定為準，截圖只作輔助紀錄）。記錄數值
- [x] 3.4 [BW-MOBILE-003] 由使用者在瀏覽器手動登入（含驗證碼，自動化工具不代為完成），以 1.1 選定的活動、W = 390 與 W = 320 以 `REQUIRE_COUNT = true` 各重跑一次：兩種寬度都版面通過，且 `namedRights.countTicketRows` 與 `namedRights.summary` 皆非空、每個值 ≤ W（Scenario 指定的計數購票列與送出訂單區塊右緣）。使用者登入前，本項維持未勾選。量測期間若 `missing` 持續非空，先確認外層頁面是否仍為登入狀態（refresh 失敗時會清掉 refresh token，等於登出），必要時重新登入再重跑
  > **3.4 初次紀錄（2026-10-08，已被下方「第 3 節最終量測紀錄」取代：當時的 namedRights 只量元素外框，等於欄寬，不能作為內容未溢出的證據；已登入買家「Buyer Test」，`REQUIRE_COUNT = true`，等待 5000ms）**：`missing` 為空（含 1.1 延後的 BW-MOBILE-003 前置確認，計數列四個項目都可見）、`outOfViewport` 為空。W = 390：scrollWidth 390、countTicketRows `[374]`、summary `[374]`；W = 320：scrollWidth 320、countTicketRows `[304]`、summary `[304]`。兩種寬度皆 `isLayoutPass === true`。修改前無已登入基準。
  >
  > **補充（strict-reviewer 指出計數列沒有修改前基準）**：未 stash，改在 iframe 內注入 `.count-ticket-row { flex-wrap: nowrap !important; }` 模擬修改前。原測試資料（分區「站票」）在 W = 320／390 不換行也放得下，無法觸發「放不下時換行」。經使用者同意，在開發 DB 為同一活動新增計數票種 `0b1a7e57-1000-4000-8000-00000000c0de`（`ZoneCode = VIP_STANDING_AREA_NORTH_WING`、價格 9,999,999、可售 99,999；名稱沒有斷行點）。結果：
  >
  > | W | 計數列 | scrollWidth | outOfViewport | 長名稱列 |
  > | --- | --- | --- | --- | --- |
  > | 320 | 強制 nowrap（模擬修改前） | 566 | 價格、可售數量、張數輸入超出 | scrollWidth 550 > 列寬 288 |
  > | 320 | 實際樣式（wrap） | 320 | 空 | 換行，列高 49 → 85 |
  > | 390 | 強制 nowrap（模擬修改前） | 566 | 同上 | scrollWidth 550 > 列寬 358 |
  >
  > 量測抓得到計數列溢出，`flex-wrap` 也確實修正了這個問題。新增資料後，以正式腳本（`REQUIRE_COUNT = true`，2 列計數票種）重跑 3.4：W = 390 和 320 都是 `missing` 為空、`isLayoutPass === true`，countTicketRows 為 `[374, 374]`／`[304, 304]`，summary 為 `[374]`／`[304]`。3.2／3.3／3.5／3.6 也用新資料重跑六種寬度（320／390／720／721／800／1280），全部 `isLayoutPass === true`，gridColumns 與先前紀錄相同。
- [x] 3.5 [BW-MOBILE-004] 以 W = 721、W = 800 與 W = 1280 各重跑一次，三個寬度都必須符合：`infoColumnWidth` 為 320，`purchaseColumnLeft` > `infoColumnRight`（購票欄在資訊欄右側），`gridColumns.length === 2` 且 `gridColumns[0] === '320px'`（與 1.2 的基準值相同），且版面通過（W = 721 是雙欄的最小寬度，購票欄最窄，最容易出現 `outOfViewport`）
- [x] 3.6 [BW-MOBILE-005] 以 W = 720 重跑：`gridColumns.length === 1`，`purchaseColumnTop` ≥ `infoColumnBottom`（購票欄在資訊欄下方，垂直順序與不重疊一併驗證），且版面通過
- [x] 3.7 [BW-MOBILE-001～003 補強：Requirement「完整顯示」] 以 `INJECT_LONG_TEXT = true`、`REQUIRE_COUNT = true`，W = 320、390、720、721 各跑一次（標題換成無斷點長網址、新增含長網址的描述、計數票種名稱與座位分區標題換成 50 字元無斷點字串）：每個寬度都版面通過，且 `namedRights` 每個值 ≤ W。修改前（未加 `overflow-wrap`）須先確認同樣注入會失敗，證明檢查有效

  > **第 3 節最終量測紀錄（2026-10-08，第二輪審查後，已登入買家「Buyer Test」，活動 `a1a3e2b4-…`，含長名稱計數票種，`REQUIRE_COUNT = true`）**：腳本為上方現行版本（namedRights 改量內容右緣、outOfViewport 加入元素內容溢出檢查、新增 `INJECT_LONG_TEXT`）。第一輪紀錄的 namedRights 只量元素外框，數值恆等於欄寬，已作廢；先前的判定只靠 `scrollWidth` 成立。所有寬度 `clientWidth === W`、`missing` 為空、`outOfViewport` 為空。
  >
  > | Task | W | INJECT | scrollWidth | gridColumns | isLayoutPass | 判定數值 |
  > | --- | --- | --- | --- | --- | --- | --- |
  > | 3.3 | 320 | false | 320 | `288px` | true | quickPickButtonTop 600 > quickPickSelectTop 520；countTicketRows `[304, 304]`、summary `[304]` |
  > | 3.2／3.4 | 390 | false | 390 | `358px` | true | title `[374]`、priceTable `[374]`、countTicketRows `[374, 374]`、summary `[374]` |
  > | 3.6 | 720 | false | 720 | `688px` | true | 單欄；purchaseColumnTop 404 ≥ infoColumnBottom 372 |
  > | 3.5 | 721 | false | 721 | `320px 337px` | true | infoColumnWidth 320；purchaseColumnLeft 368 > infoColumnRight 336 |
  > | 3.5 | 800 | false | 800 | `320px 416px` | true | infoColumnWidth 320；purchaseColumnLeft 368 > infoColumnRight 336 |
  > | 3.5 | 1280 | false | 1280 | `320px 728px` | true | infoColumnWidth 320；purchaseColumnLeft 452 > infoColumnRight 420 |
  > | 3.7 | 320 | true | 320 | `288px` | true | title `[304]`、priceTable `[304]`、countTicketRows `[304]`、summary `[304]` |
  > | 3.7 | 390 | true | 390 | `358px` | true | title `[374]`、priceTable `[374]`、countTicketRows `[374]`、summary `[374]` |
  > | 3.7 | 720 | true | 720 | `688px` | true | title `[704]`、priceTable `[704]`、countTicketRows `[704]`、summary `[704]` |
  > | 3.7 | 721 | true | 721 | `320px 337px` | true | title `[336]`、priceTable `[336]`、countTicketRows `[705]`、summary `[705]` |
  >
  > 3.7 四列是第三輪審查後用現行腳本重量的（含注入內容自我檢查，`missing` 為空代表注入內容在量測當下仍在、描述段落套用了 `white-space: pre-wrap`）。重量前已依使用者指示刪除長名稱測試票種，所以計數列只剩「站票」一列；長名稱改由注入涵蓋。3.2～3.6 的數值是刪除前量的。
  >
  > **3.7 修改前（未加 `overflow-wrap`）**：W = 390 同樣注入，scrollWidth 653、`isLayoutPass === false`，namedRights.title `[653]`、countTicketRows `[522, 522]`，outOfViewport 列出 h1（scrollWidth 637 > clientWidth 358）、`.description`、`.zone-block h3`、`.count-ticket-name` 等，證明新檢查會失敗。（這次量測用的是修改中的腳本版本。）
  >
  > **以現行腳本重現失敗**：`FORCE_NO_OVERFLOW_WRAP = true`、W = 390 同樣注入：`isLayoutPass === false`、scrollWidth 653、`missing` 為空（注入自我檢查通過），outOfViewport 列出 `aside.info-column`、h1（scrollWidth 637 > clientWidth 358）、`p.description`（scrollWidth 448 > clientWidth 358）、`section.purchase-column` 等。現行腳本在沒有修正時確實會失敗。
  >
  > **`overflow-wrap: anywhere` 的副作用檢查**：W = 390 與 1280 時，比較加與不加（iframe 內注入 `overflow-wrap: normal !important`）每個葉節點文字的高度。只有票價表中長分區名稱那一格不同：不加時是單行 24px，名稱被儲存格裁切；加了之後換成多行 120px，完整顯示。其他文字都沒有改變。第三輪審查後補量 W = 320 與 721（雙欄最窄），不注入、使用真實資料（長名稱測試票種已刪除），比較 `.layout` 內全部 103 個元素的寬高：加與不加完全相同，0 個差異。
  >
  > **已知量測偏差**：腳本隱藏捲軸，模擬覆蓋式捲軸。桌面瀏覽器的傳統捲軸會佔 15px，所以 W = 721～735 時實際內容寬度比 media query 判定的寬度少 15px（721 時購票欄是 322px，不是 337px）。依各元素的最小寬度推算仍放得下，但沒有實測（見 design.md 決策 3）。

## 4. 回歸

- [x] 4.1 執行 `docker compose exec web npm run test`，前端測試全數通過（特別是既有的 `EventDetailPage.test.ts`）；有失敗時附上輸出，不得以「與本次無關」為由略過不報
  > **4.1 紀錄（2026-10-08）**：第一次完整執行 2 failed／439 passed：`EventListPage.test.ts`「關閉開關但可售總量填 0…」與 `EventDetailPage.test.ts`「純計數票種的名稱跟座位分區同名時…」。單獨執行這兩個檔案 90／90 通過，之後再完整執行兩次都是 441／441 通過。判定為全套負載下的間歇性失敗（本次只改 style 與 `lang`），第一次失敗的錯誤訊息沒有保留，根因未查。
  >
  > **第二輪（加入 `overflow-wrap` 後重跑）**：第一次完整執行 4 failed／437 passed，失敗的測試和第一輪不同，這次保留了錯誤訊息：
  > - `EventCreatePage.test.ts`「[AWU-EVENT-SALES-002] 選了開賣、停售時間後清空…」：`expected "vi.fn()" to be called 1 times, but got 0 times`
  > - `EventDetailPage.test.ts`「未登入嘗試調整計數購買數量：立即導向登入頁…」：`expected "vi.fn()" to be called with arguments: [ { path: '/login', …(1) } ]`
  > - `EventDetailPage.test.ts`「純計數購買數量達到每筆訂單限購張數…」：`expected '5' to be '1'`
  > - `OrderDetailPage.test.ts`「[BW-PENDING-012] 付款被拒（409）…」：`expected "vi.fn()" to be called 2 times, but got 1 times`
  >
  > 這三個檔案單獨執行 130／130 通過，之後連續完整執行三次都是 441／441 通過。失敗都是呼叫次數或非同步結果的斷言，且涉及本 change 沒有修改的頁面（EventCreatePage、OrderDetailPage）。判定為既有的、與負載相關的間歇性失敗，沒有在本 change 處理；建議另開項目追蹤。
  >
  > **第三輪（strict-reviewer 執行）**：第一次 6 failed／435 passed（4 個檔案），另有 1 個 unhandled rejection（`OrderDetailPage.test.ts` 的 ApiError「付款被拒」）；第二次 441／441 通過。
  >
  > **基準對照（2026-10-08）**：以 `git stash push -u -- web/` 移除本 change 的全部前端修改，在 HEAD（6ac5298）連續完整執行 5 次：2 次失敗。第 1 次 4 failed／436 passed（`RegisterPage` CAPTCHA-BW-002、`RealNamePage` BW-RN-PAGE-003 與 011、`EventCreatePage`「改選另一個場館，座位圖選擇值被清除」）；第 4 次 2 failed／438 passed（`EventDetailPage` BW-QUEUE-005、`EventListPage`「展開票種清單，正確顯示既有票種的模式與可售總量」）；其餘 3 次 440／440 通過。之後已 `git stash pop` 還原並確認 diff 不變。結論：間歇性失敗在沒有本 change 時就存在，每次失敗的測試都不同，與本 change 無關。本 change 的測試（含 `indexHtml.test.ts`）在每次完整執行中都通過。
- [x] 4.2 執行 `docker compose exec web npm run build`（含 `vue-tsc -b`），確認可建置，尤其確認 2.1 的 `?raw` import 能通過型別檢查
- [x] 4.3 更新 `docs/ui-ux-review-2026-10-08.md`：將 R3 與 4.2 節的 `lang` 項目標註為已修正，並附上本 change 名稱；第 7 節「仍未實測」新增一項：排隊驗證碼畫面（`.captcha-row`）的窄螢幕量測，留給買家探索頁改版 change 處理
