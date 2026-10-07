> 所有指令都在容器內執行（例如 `docker compose exec web npm run test`），不在本機執行 node／npm。
>
> **測試種類與 AC 對應**（理由見 design.md 決策 3）。
>
> **AC 測試對應例外**：BW-MOBILE-001～005 以瀏覽器量測取代 CLAUDE.md 要求的單元或整合測試，例外紀錄見 design.md「AC 測試對應例外」。核准狀態：已核准（2026-10-08）。
>
> | ID | Scenario | 驗證方式 | Task |
> | --- | --- | --- | --- |
> | BW-MOBILE-001 | 390px 寬未登入瀏覽活動詳情頁 | 瀏覽器量測腳本 | 1.2（基準值）、3.2 |
> | BW-MOBILE-002 | 320px 寬未登入瀏覽活動詳情頁 | 瀏覽器量測腳本 | 1.2（基準值）、3.3 |
> | BW-MOBILE-003 | 已登入買家在窄螢幕瀏覽含計數票種的活動詳情頁（390／320） | 瀏覽器量測腳本（使用者手動登入） | 3.4 |
> | BW-MOBILE-004 | 桌面與平板寬度維持雙欄版面（721／800／1280） | 瀏覽器量測腳本 | 1.2（基準值）、3.5 |
> | BW-MOBILE-005 | 斷點上限寬度維持單欄且不橫向捲動（720） | 瀏覽器量測腳本 | 1.2（基準值）、3.6 |
> | BW-LANG-001 | 根元素語言宣告 | Vitest 單元測試（`?raw` 讀檔，不碰 DB 與網路） | 2.1 |
>
> **不新增整合測試**：本 change 不改後端與 API。

## 1. 修改前基準量測

- [ ] 1.1 選定測試活動並記錄其 id：兩者都必須是**非排隊（非熱門搶購）模式**，否則 `.quick-pick` 不會渲染。未登入量測用「含座位制票種」的活動；BW-MOBILE-003 用「同時有座位制與計數票種、且販售中」的活動（可與前者相同）。此外，兩者都必須**實際已產生座位資料**（座位區塊由座位資料分組渲染，見 `EventDetailPage.vue` 的 `seatsByZone`；只有座位制票種、沒有座位資料時不會出現 `.zone-block` 與 `.seat-btn`）。BW-MOBILE-003 的活動也必須至少有一個計數票種。選定後，先以該活動 id 跑一次量測腳本（BW-MOBILE-003 的活動須在登入後以 `REQUIRE_COUNT = true` 執行；若此時使用者尚未登入，這項確認延到 3.4 開始量測前進行，1.1 仍可先勾選未登入的部分），確認 `missing` 為空，證明測試資料符合前置條件。若沒有符合條件的活動，先回報，再決定測試資料，不得跳過。之後所有量測都使用同一組 id
- [ ] 1.2 在目前 Vite dev server 的 origin（`web/certs` 有憑證時為 `https://localhost:5173`，否則為 `http://localhost:5173`，見 `web/vite.config.ts`）的任一頁，以 javascript 執行下方量測腳本，W 分別為 320、390、720、721、800、1280，記錄每次的回傳值。**預期 390 與 320 會重現 `scrollWidth` > W（報告實測 390 時為 470）**；721 依推算（購票欄約 337px，小於 `.quick-pick` 約 452px 的最小寬度）也可能溢出，照實記錄即可，不作為停止條件。若 390 或 320 沒有重現，代表腳本或測試資料有誤，先停下排查，不得進入第 3 節。若使用者此時已登入，也對 BW-MOBILE-003 的活動以 W = 320 記錄一次修改前的 `outOfViewport`（`REQUIRE_COUNT = true`），作為量測能抓到計數列溢出的證據；未登入時則註記「無已登入基準」

  量測腳本（`EVENT_ID`、`W`、`REQUIRE_COUNT` 依情況替換；`REQUIRE_COUNT` 只有 3.4 已登入量測時為 `true`）：

  ```js
  (async (EVENT_ID, W, REQUIRE_COUNT) => {
    const frame = document.createElement('iframe');
    frame.style.cssText = `position:fixed;top:0;left:0;width:${W}px;height:10000px;border:0;z-index:99999;background:#fff`;
    frame.src = `/events/${EVENT_ID}`;
    document.body.appendChild(frame);
    await new Promise((resolve) => frame.addEventListener('load', resolve, { once: true }));
    await new Promise((resolve) => setTimeout(resolve, 2500));
    const doc = frame.contentDocument;
    // 量測基準一律是 W（與 spec 的視窗寬度一致）。iframe 加高到內容不會產生垂直捲軸，模擬手機覆蓋式捲軸不佔寬度的情境；
    // 若仍出現捲軸（clientWidth < W），0～15px 的溢出會被捲軸吸收，因此列為 missing，使該次量測無效
    const clientWidth = doc.documentElement.clientWidth;
    const viewportMismatch = clientWidth === W ? [] : [`clientWidth=${clientWidth} ≠ W（出現垂直捲軸，請加高 iframe 後重量）`];
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
    // 計數購票列逐列確認四個項目（區域、價格、可售數量、張數輸入）都存在且可見，只確認容器不足以證明內容完整顯示
    if (REQUIRE_COUNT) {
      const countRowParts = ['.count-ticket-name', '.count-ticket-price', '.count-ticket-quantity', '.el-input-number'];
      doc.querySelectorAll('.count-ticket-row').forEach((row, index) => {
        countRowParts
          .filter((part) => !isVisibleRect(row.querySelector(part)?.getBoundingClientRect() ?? null))
          .forEach((part) => missing.push(`.count-ticket-row[${index}] ${part}`));
      });
    }
    // 版面內每個可見元素都不得超出視窗右緣，涵蓋規格要求完整顯示的所有內容（含時間、販售期間、座位區塊）
    const outOfViewport = [...doc.querySelectorAll('.layout *')]
      .map((el) => ({ el, rect: el.getBoundingClientRect() }))
      .filter(({ rect }) => rect.width > 0 && rect.height > 0 && Math.round(rect.right) > W)
      .slice(0, 10)
      .map(({ el, rect }) => `${el.tagName.toLowerCase()}.${[...el.classList].join('.')} right=${Math.round(rect.right)}`);
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
      // Scenario 指名的元素逐一輸出右緣，供 task 直接追溯（每個陣列都必須非空且每個值 ≤ W）
      namedRights: Object.fromEntries(
        [
          ['title', '.info-column h1'],
          ['priceTable', '.info-column .el-table'],
          ['countTicketRows', '.count-ticket-row'],
          ['summary', '.summary'],
        ].map(([name, selector]) => [name, [...doc.querySelectorAll(selector)].map((el) => Math.round(el.getBoundingClientRect().right))]),
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
  })('EVENT_ID', W, false);
  ```

  > **每次量測的有效條件**：`missing` 必須是空陣列。若不是，代表頁面尚未載入完成、測試活動不符條件（例如排隊模式不會渲染 `.quick-pick`），或已登入量測時登入狀態尚未還原，**該次量測無效，不得用來判定通過或失敗**。登入狀態要經過 refresh 還原，會多一次網路往返，此時把等待時間從 2500 延長到 5000 再量一次。缺少元素時，相關數值欄位為 `NaN`，與任何數字比較都不成立；不過判定一律以 `missing` 為準，不依賴這個性質。
  >
  > 以下「版面通過」指腳本回傳 `isLayoutPass === true`，即 `missing` 為空、`outOfViewport` 為空，且 `scrollWidth` ≤ W。`missing` 也包含「`clientWidth` 必須等於 W」的檢查，確保量測基準與 spec 的視窗寬度一致（見腳本註解）。

## 2. 語言宣告（BW-LANG-001）

- [ ] 2.1 新增 `web/src/indexHtml.test.ts`：以 `import indexHtml from '../index.html?raw'` 取得內容，用 `new DOMParser().parseFromString(indexHtml, 'text/html')` 解析，斷言 `documentElement.lang` 為 `zh-Hant-TW`，測試名稱前綴 `[BW-LANG-001]`。**不得使用 `node:fs`**（理由見 design.md 決策 3）。先執行並確認**失敗**（目前是 `en`）
- [ ] 2.2 `web/index.html`：`lang="en"` 改為 `lang="zh-Hant-TW"`，重跑 2.1 確認通過

## 3. 活動詳情頁版面（BW-MOBILE-001～005）

- [ ] 3.1 修改 `web/src/pages/buyer/EventDetailPage.vue` 的 `<style scoped>`（design.md 決策 1），不修改 template 與 script：
  - `.layout` 改為 `grid-template-columns: 320px minmax(0, 1fr)`
  - `@media (max-width: 720px)` 內改為 `minmax(0, 1fr)`
  - `.quick-pick` 加上 `flex-wrap: wrap`
  - `.count-ticket-row` 加上 `flex-wrap: wrap`
- [ ] 3.2 [BW-MOBILE-001] 以 W = 390 重跑量測腳本：版面通過，且 `namedRights.title` 與 `namedRights.priceTable` 皆非空、每個值 ≤ 390（Scenario 指定的標題與票價表右緣）。記錄數值
- [ ] 3.3 [BW-MOBILE-002] 以 W = 320 重跑：版面通過，且 `quickPickButtonTop` > `quickPickSelectTop`（按鈕換到下一行；以此幾何判定為準，截圖只作輔助紀錄）。記錄數值
- [ ] 3.4 [BW-MOBILE-003] 由使用者在瀏覽器手動登入（含驗證碼，自動化工具不代為完成），以 1.1 選定的活動、W = 390 與 W = 320 以 `REQUIRE_COUNT = true` 各重跑一次：兩種寬度都版面通過，且 `namedRights.countTicketRows` 與 `namedRights.summary` 皆非空、每個值 ≤ W（Scenario 指定的計數購票列與送出訂單區塊右緣）。使用者登入前，本項維持未勾選。量測期間若 `missing` 持續非空，先確認外層頁面是否仍為登入狀態（refresh 失敗時會清掉 refresh token，等於登出），必要時重新登入再重跑
- [ ] 3.5 [BW-MOBILE-004] 以 W = 721、W = 800 與 W = 1280 各重跑一次，三個寬度都必須符合：`infoColumnWidth` 為 320，`purchaseColumnLeft` > `infoColumnRight`（購票欄在資訊欄右側），`gridColumns.length === 2` 且 `gridColumns[0] === '320px'`（與 1.2 的基準值相同），且版面通過（W = 721 是雙欄的最小寬度，購票欄最窄，最容易出現 `outOfViewport`）
- [ ] 3.6 [BW-MOBILE-005] 以 W = 720 重跑：`gridColumns.length === 1`，`purchaseColumnTop` ≥ `infoColumnBottom`（購票欄在資訊欄下方，垂直順序與不重疊一併驗證），且版面通過

## 4. 回歸

- [ ] 4.1 執行 `docker compose exec web npm run test`，前端測試全數通過（特別是既有的 `EventDetailPage.test.ts`）；有失敗時附上輸出，不得以「與本次無關」為由略過不報
- [ ] 4.2 執行 `docker compose exec web npm run build`（含 `vue-tsc -b`），確認可建置，尤其確認 2.1 的 `?raw` import 能通過型別檢查
- [ ] 4.3 更新 `docs/ui-ux-review-2026-10-08.md`：將 R3 與 4.2 節的 `lang` 項目標註為已修正，並附上本 change 名稱；第 7 節「仍未實測」新增一項：排隊驗證碼畫面（`.captcha-row`）的窄螢幕量測，留給買家探索頁改版 change 處理
