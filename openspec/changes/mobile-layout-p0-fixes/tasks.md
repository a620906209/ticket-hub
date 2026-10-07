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

- [ ] 1.1 選定測試活動並記錄其 id：兩者都必須是**非排隊（非熱門搶購）模式**，否則 `.quick-pick` 不會渲染。未登入量測用「含座位制票種」的活動；BW-MOBILE-003 用「同時有座位制與計數票種、且販售中」的活動（可與前者相同）。若沒有符合條件的活動，先回報，再決定測試資料，不得跳過。之後所有量測都使用同一組 id
- [ ] 1.2 在目前 Vite dev server 的 origin（`web/certs` 有憑證時為 `https://localhost:5173`，否則為 `http://localhost:5173`，見 `web/vite.config.ts`）的任一頁，以 javascript 執行下方量測腳本，W 分別為 320、390、720、721、800、1280，記錄每次的回傳值。**預期 390 與 320 會重現 `scrollWidth` > W（報告實測 390 時為 470）**；721 依推算（購票欄約 337px，小於 `.quick-pick` 約 452px 的最小寬度）也可能溢出，照實記錄即可，不作為停止條件。若 390 或 320 沒有重現，代表腳本或測試資料有誤，先停下排查，不得進入第 3 節。若使用者此時已登入，也對 BW-MOBILE-003 的活動以 W = 320 記錄一次修改前的 `countTicketRowRight`，作為量測能抓到計數列溢出的證據；未登入時則註記「無已登入基準」

  量測腳本（`EVENT_ID` 與 `W` 依情況替換）：

  ```js
  (async (EVENT_ID, W) => {
    const frame = document.createElement('iframe');
    frame.style.cssText = `position:fixed;top:0;left:0;width:${W}px;height:844px;border:0;z-index:99999;background:#fff`;
    frame.src = `/events/${EVENT_ID}`;
    document.body.appendChild(frame);
    await new Promise((resolve) => frame.addEventListener('load', resolve, { once: true }));
    await new Promise((resolve) => setTimeout(resolve, 2500));
    const doc = frame.contentDocument;
    const rightOf = (selector) => [...doc.querySelectorAll(selector)].map((el) => Math.round(el.getBoundingClientRect().right));
    const layout = doc.querySelector('.layout');
    const result = {
      W,
      scrollWidth: doc.documentElement.scrollWidth,
      gridTemplateColumns: layout ? getComputedStyle(layout).gridTemplateColumns : null,
      infoColumnRight: Math.round(doc.querySelector('.info-column')?.getBoundingClientRect().right ?? -1),
      infoColumnBottom: Math.round(doc.querySelector('.info-column')?.getBoundingClientRect().bottom ?? -1),
      purchaseColumnTop: Math.round(doc.querySelector('.purchase-column')?.getBoundingClientRect().top ?? -1),
      purchaseColumnLeft: Math.round(doc.querySelector('.purchase-column')?.getBoundingClientRect().left ?? -1),
      infoColumnWidth: Math.round(doc.querySelector('.info-column')?.getBoundingClientRect().width ?? -1),
      titleRight: rightOf('.info-column h1'),
      priceTableRight: rightOf('.info-column table, .info-column .el-table'),
      quickPickRight: rightOf('.quick-pick'),
      quickPickSelectTop: Math.round(doc.querySelector('.quick-pick .el-select')?.getBoundingClientRect().top ?? -1),
      quickPickButtonTop: Math.round(doc.querySelector('.quick-pick .el-button')?.getBoundingClientRect().top ?? -1),
      countTicketRowRight: rightOf('.count-ticket-row'),
      summaryRight: rightOf('.summary'),
    };
    frame.remove();
    return result;
  })('EVENT_ID', W);
  ```

  > 已登入的量測（3.4）也使用此腳本：iframe 與父頁同源，共用登入狀態。若頁面尚未登入，`countTicketRowRight` 與 `summaryRight` 會是空陣列，此時該次量測無效：登入狀態要經過 refresh 還原，會多一次網路往返，請把等待時間從 2500 延長到 5000 後重量。

## 2. 語言宣告（BW-LANG-001）

- [ ] 2.1 新增 `web/src/indexHtml.test.ts`：以 `import indexHtml from '../index.html?raw'` 取得內容，用 `new DOMParser().parseFromString(indexHtml, 'text/html')` 解析，斷言 `documentElement.lang` 為 `zh-Hant-TW`，測試名稱前綴 `[BW-LANG-001]`。**不得使用 `node:fs`**（理由見 design.md 決策 3）。先執行並確認**失敗**（目前是 `en`）
- [ ] 2.2 `web/index.html`：`lang="en"` 改為 `lang="zh-Hant-TW"`，重跑 2.1 確認通過

## 3. 活動詳情頁版面（BW-MOBILE-001～005）

- [ ] 3.1 修改 `web/src/pages/buyer/EventDetailPage.vue` 的 `<style scoped>`（design.md 決策 1），不修改 template 與 script：
  - `.layout` 改為 `grid-template-columns: 320px minmax(0, 1fr)`
  - `@media (max-width: 720px)` 內改為 `minmax(0, 1fr)`
  - `.quick-pick` 加上 `flex-wrap: wrap`
  - `.count-ticket-row` 加上 `flex-wrap: wrap`
- [ ] 3.2 [BW-MOBILE-001] 以 W = 390 重跑量測腳本：`scrollWidth` ≤ 390，`titleRight`、`priceTableRight`、`quickPickRight` 皆 ≤ 390。記錄數值
- [ ] 3.3 [BW-MOBILE-002] 以 W = 320 重跑：`scrollWidth` ≤ 320，各項右緣皆 ≤ 320，且 `quickPickButtonTop` > `quickPickSelectTop`（按鈕換到下一行；以此幾何判定為準，截圖只作輔助紀錄）。記錄數值
- [ ] 3.4 [BW-MOBILE-003] 由使用者在瀏覽器手動登入（含驗證碼，自動化工具不代為完成），以 1.1 選定的活動、W = 390 與 W = 320 各重跑一次：`scrollWidth` ≤ W，`countTicketRowRight` 與 `summaryRight` 非空且皆 ≤ W。使用者登入前，本項維持未勾選
- [ ] 3.5 [BW-MOBILE-004] 以 W = 721、W = 800 與 W = 1280 各重跑一次，三個寬度都必須符合：`infoColumnWidth` 為 320，`purchaseColumnLeft` > `infoColumnRight`（購票欄在資訊欄右側），`gridTemplateColumns` 第一欄為 `320px`（與 1.2 的基準值相同），且 `scrollWidth` ≤ W；另外 W = 721 是雙欄的最小寬度，購票欄最窄，該次還須 `quickPickRight` ≤ 721
- [ ] 3.6 [BW-MOBILE-005] 以 W = 720 重跑：`gridTemplateColumns` 只有一欄（不含空格分隔的第二個值），`purchaseColumnTop` ≥ `infoColumnBottom`（購票欄在資訊欄下方，垂直順序與不重疊一併驗證），且 `scrollWidth` ≤ 720

## 4. 回歸

- [ ] 4.1 執行 `docker compose exec web npm run test`，前端測試全數通過（特別是既有的 `EventDetailPage.test.ts`）；有失敗時附上輸出，不得以「與本次無關」為由略過不報
- [ ] 4.2 執行 `docker compose exec web npm run build`（含 `vue-tsc -b`），確認可建置，尤其確認 2.1 的 `?raw` import 能通過型別檢查
- [ ] 4.3 更新 `docs/ui-ux-review-2026-10-08.md`：將 R3 與 4.2 節的 `lang` 項目標註為已修正，並附上本 change 名稱；第 7 節「仍未實測」新增一項：排隊驗證碼畫面（`.captcha-row`）的窄螢幕量測，留給買家探索頁改版 change 處理
