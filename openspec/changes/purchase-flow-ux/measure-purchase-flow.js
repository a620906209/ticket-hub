// purchase-flow-ux 版面量測腳本（design.md 決策 6、tasks.md 1.1）。
// 執行方式：依 tasks.md 1.1「登入／登出前置」建立該輪登入狀態後，於前端 dev server 的同源分頁以
// claude-in-chrome 的 javascript_tool 執行本檔全文。
// - EVENT_ID：取自 loadtest/fixtures/purchase-flow-ux-event.js 輸出的 PURCHASE_FLOW_UX_EVENT_ID。
// - BASELINE = true：修改前的基準量測，結帳列為 .summary、圖例色塊尚未存在。
// - EXPECT_AUTHENTICATED = true：量全部寬度與斷言 1 至 4；false：只量 320／390／720 的斷言 4。
// 回傳 JSON 字串（陣列），每筆對應一個寬度；斷言未執行時為 null。
(async () => {
  const EVENT_ID = 'b4b9076b-cc48-44e7-92cc-3807f04500e0';
  const BASELINE = false;
  const EXPECT_AUTHENTICATED = false;

  const FRAME_HEIGHT = 700;
  const MIN_SEAT_COUNT = 400;
  const SINGLE_COLUMN_MAX_WIDTH = 720;
  const INFO_COLUMN_WIDTH = 320;
  const targetPath = `/events/${EVENT_ID}`;
  const widths = EXPECT_AUTHENTICATED ? [320, 390, 720, 721, 800, 1280] : [320, 390, 720];
  const focusAssertionWidths = [390, 1280];

  // 只允許這一種 storage 存取，且在頁面內直接算成布林值（tasks.md 1.1「敏感資訊界線」）。
  const hasRefreshToken = () => localStorage.getItem('ticketing.refreshToken') !== null;

  // 背景分頁不會觸發 requestAnimationFrame，一律用 setTimeout 等待
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  async function waitForSelector(doc, selector, timeoutMs) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
      if (doc.querySelector(selector)) return true;
      await sleep(100);
    }
    return false;
  }

  function toRect(el) {
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { top: r.top, bottom: r.bottom, left: r.left, right: r.right, width: r.width, height: r.height };
  }

  function seatId(el) {
    return el ? el.textContent.trim() : null;
  }

  function resolveAuthState(doc) {
    const hasToken = hasRefreshToken();
    const ordersLink = doc.querySelector('.buyer-header a[href="/orders"]');
    const loginLink = doc.querySelector('.buyer-header a[href="/login"]');
    const memberTrigger = doc.querySelector('.member-trigger');
    const isLoggedIn = hasToken && ordersLink !== null && memberTrigger !== null &&
      memberTrigger.textContent.trim() !== '' && loginLink === null;
    const isLoggedOut = !hasToken && loginLink !== null && memberTrigger === null;
    if (isLoggedIn) return true;
    if (isLoggedOut) return false;
    return null;
  }

  async function measure(W) {
    const frame = document.createElement('iframe');
    frame.style.cssText = `position:fixed;top:0;left:0;width:${W}px;height:${FRAME_HEIGHT}px;border:0;z-index:99999;background:#fff`;
    frame.src = targetPath;
    document.body.appendChild(frame);
    try {
      await new Promise((resolve) => frame.addEventListener('load', resolve, { once: true }));
      const win = frame.contentWindow;
      const doc = frame.contentDocument;
      // 量測基準一律是 W：隱藏 iframe 捲軸（仍可捲動），模擬手機覆蓋式捲軸不佔寬度
      doc.documentElement.style.scrollbarWidth = 'none';
      await waitForSelector(doc, '.zone-block .seat-btn', 15000);
      await waitForSelector(doc, '.buyer-header .member-trigger, .buyer-header a[href="/login"]', 15000);
      await sleep(1000);

      const invalidReasons = [];
      const numbers = [];
      const track = (value) => { numbers.push(value); return value; };
      const trackRect = (rect) => {
        if (rect) { track(rect.top); track(rect.bottom); track(rect.left); track(rect.right); track(rect.width); }
        return rect;
      };
      const requireWidth = (rect, name) => {
        if (!rect) invalidReasons.push(`找不到 ${name}`);
        else if (!(rect.width > 0)) invalidReasons.push(`${name} 寬度 ≤ 0`);
      };

      const pathname = win.location.pathname;
      if (pathname !== targetPath) invalidReasons.push(`pathname=${pathname} ≠ ${targetPath}`);
      const clientWidth = track(doc.documentElement.clientWidth);
      if (clientWidth !== W) invalidReasons.push(`clientWidth=${clientWidth} ≠ W`);
      const isAuthenticated = resolveAuthState(doc);
      if (isAuthenticated === null) invalidReasons.push('登入狀態無法判定');
      else if (isAuthenticated !== EXPECT_AUTHENTICATED) invalidReasons.push(`isAuthenticated=${isAuthenticated} ≠ EXPECT_AUTHENTICATED`);

      const zone = doc.querySelector('.zone-block');
      if (!zone) invalidReasons.push('找不到 .zone-block');
      const zoneSeats = zone ? [...zone.querySelectorAll('.seat-btn')] : [];
      const seatCount = zoneSeats.length;
      if (seatCount < MIN_SEAT_COUNT) invalidReasons.push(`第一個分區座位數=${seatCount} < ${MIN_SEAT_COUNT}`);
      const soldSeat = zoneSeats.find((el) => el.classList.contains('sold')) ?? null;
      const availableSeats = zoneSeats.filter((el) => !el.classList.contains('sold'));
      if (!soldSeat) invalidReasons.push('第一個分區找不到 .seat-btn.sold');
      if (availableSeats.length === 0) invalidReasons.push('第一個分區找不到 Available 座位');
      const lastSeat = zoneSeats[zoneSeats.length - 1] ?? null;

      const quickPickButton = doc.querySelector('.quick-pick button');
      const zoneSelect = doc.querySelector('.quick-pick .el-select');
      if (!quickPickButton) invalidReasons.push('找不到 .quick-pick button');
      const checkoutBar = doc.querySelector(BASELINE ? '.summary' : '.checkout-bar');
      const submitButton = checkoutBar
        ? [...checkoutBar.querySelectorAll('button')].find((el) => el.textContent.trim() === '送出訂單') ?? null
        : null;
      const countRow = doc.querySelector('.count-ticket-row');
      if (EXPECT_AUTHENTICATED) {
        if (!checkoutBar) invalidReasons.push(`找不到 ${BASELINE ? '.summary' : '.checkout-bar'}`);
        if (!submitButton) invalidReasons.push('找不到送出按鈕');
        if (!countRow) invalidReasons.push('找不到 .count-ticket-row');
      }

      // 斷言 1：捲到第一個分區頂端後，送出按鈕完全在視窗內，且最後一個座位在視窗下緣之下
      let assertion1 = null;
      let submitButtonRect = null;
      let lastSeatTop = null;
      if (EXPECT_AUTHENTICATED && zone && submitButton && lastSeat) {
        win.scrollTo(0, win.scrollY + zone.getBoundingClientRect().top);
        await sleep(300);
        submitButtonRect = trackRect(toRect(submitButton));
        requireWidth(submitButtonRect, '送出按鈕');
        lastSeatTop = track(lastSeat.getBoundingClientRect().top);
        assertion1 = submitButtonRect.top >= 0 && submitButtonRect.bottom <= FRAME_HEIGHT && lastSeatTop > FRAME_HEIGHT;
      }

      // 斷言 2：在第一個可選座位按 End，焦點移到最後一個可選座位，且不被結帳列遮住
      let assertion2 = null;
      let lastAvailableSeatRectBefore = null;
      let focusedSeatId = null;
      let expectedFocusedSeatId = null;
      let focusedSeatRect = null;
      let checkoutBarRect = null;
      if (EXPECT_AUTHENTICATED && focusAssertionWidths.includes(W) && availableSeats.length > 0 && checkoutBar) {
        win.scrollTo(0, 0);
        await sleep(300);
        const firstAvailable = availableSeats[0];
        const lastAvailable = availableSeats[availableSeats.length - 1];
        expectedFocusedSeatId = seatId(lastAvailable);
        lastAvailableSeatRectBefore = trackRect(toRect(lastAvailable));
        if (!(lastAvailableSeatRectBefore.top > FRAME_HEIGHT)) {
          invalidReasons.push(`最後一個 Available 座位起始 top=${lastAvailableSeatRectBefore.top} 未超出視窗下緣`);
        }
        firstAvailable.focus({ preventScroll: true });
        firstAvailable.dispatchEvent(new win.KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true }));
        await sleep(300);
        const active = doc.activeElement;
        focusedSeatId = active && active.classList.contains('seat-btn') ? seatId(active) : null;
        focusedSeatRect = trackRect(toRect(active));
        checkoutBarRect = trackRect(toRect(checkoutBar));
        requireWidth(checkoutBarRect, '結帳列');
        assertion2 = active === lastAvailable && focusedSeatRect.top >= 0 && focusedSeatRect.bottom <= checkoutBarRect.top;
      } else if (checkoutBar) {
        checkoutBarRect = trackRect(toRect(checkoutBar));
      }

      // 斷言 3：不可選座位與「不可選」色塊有刪除線與虛線，Available 座位沒有
      const styleOf = (el) => {
        if (!el) return null;
        const style = win.getComputedStyle(el);
        return { textDecorationLine: style.textDecorationLine, borderTopStyle: style.borderTopStyle };
      };
      const soldSeatStyle = styleOf(soldSeat);
      const availableSeatStyle = styleOf(availableSeats[0]);
      const unavailableSwatchStyle = styleOf(doc.querySelector('.seat-swatch--unavailable'));
      let assertion3 = null;
      if (EXPECT_AUTHENTICATED && soldSeatStyle && availableSeatStyle) {
        assertion3 = soldSeatStyle.textDecorationLine.includes('line-through') &&
          soldSeatStyle.borderTopStyle === 'dashed' &&
          unavailableSwatchStyle !== null &&
          unavailableSwatchStyle.textDecorationLine === soldSeatStyle.textDecorationLine &&
          unavailableSwatchStyle.borderTopStyle === soldSeatStyle.borderTopStyle &&
          !availableSeatStyle.textDecorationLine.includes('line-through') &&
          availableSeatStyle.borderTopStyle === 'solid';
      }

      // 斷言 4：不產生頁面橫向捲動、雙欄／單欄版面、主要區塊右緣不超出視窗
      win.scrollTo(0, 0);
      await sleep(300);
      const documentScrollWidth = track(doc.documentElement.scrollWidth);
      const layout = doc.querySelector('.layout');
      const infoColumnRect = trackRect(toRect(doc.querySelector('.info-column')));
      const purchaseColumnRect = trackRect(toRect(doc.querySelector('.purchase-column')));
      const gridColumns = layout ? win.getComputedStyle(layout).gridTemplateColumns.split(' ') : [];
      const firstGridColumnWidth = gridColumns.length > 0 ? track(parseFloat(gridColumns[0])) : null;
      const titleRect = trackRect(toRect(doc.querySelector('.info-column h1')));
      const ticketTableRect = trackRect(toRect(doc.querySelector('.info-column .el-table')));
      const countRowRect = EXPECT_AUTHENTICATED ? trackRect(toRect(countRow)) : null;
      const checkoutBarRight = EXPECT_AUTHENTICATED && checkoutBar ? track(checkoutBar.getBoundingClientRect().right) : null;
      const quickPickButtonTop = quickPickButton ? track(quickPickButton.getBoundingClientRect().top) : null;
      const zoneSelectTop = zoneSelect ? track(zoneSelect.getBoundingClientRect().top) : null;
      if (!layout) invalidReasons.push('找不到 .layout');
      requireWidth(infoColumnRect, '.info-column');
      requireWidth(purchaseColumnRect, '.purchase-column');
      requireWidth(titleRect, '活動標題');
      requireWidth(ticketTableRect, '票價表');
      if (EXPECT_AUTHENTICATED) requireWidth(countRowRect, '計數購票列');
      if (W === 320 && zoneSelectTop === null) invalidReasons.push('找不到分區選單');

      let assertion4 = null;
      if (layout && infoColumnRect && purchaseColumnRect && titleRect && ticketTableRect) {
        const isLayoutOk = W <= SINGLE_COLUMN_MAX_WIDTH
          ? gridColumns.length === 1 && purchaseColumnRect.top >= infoColumnRect.bottom
          : firstGridColumnWidth === INFO_COLUMN_WIDTH && infoColumnRect.width === INFO_COLUMN_WIDTH &&
            purchaseColumnRect.left > infoColumnRect.right;
        const rights = [titleRect.right, ticketTableRect.right];
        if (EXPECT_AUTHENTICATED) rights.push(countRowRect?.right ?? Infinity, checkoutBarRight ?? Infinity);
        const isQuickPickWrapped = W !== 320 || (quickPickButtonTop !== null && zoneSelectTop !== null && quickPickButtonTop > zoneSelectTop);
        assertion4 = documentScrollWidth <= W && isLayoutOk && rights.every((right) => right <= W) && isQuickPickWrapped;
      }

      if (numbers.some((value) => !Number.isFinite(value))) invalidReasons.push('有量測值不是有限數值');

      return {
        W, baseline: BASELINE, expectAuthenticated: EXPECT_AUTHENTICATED, pathname, isAuthenticated, seatCount,
        documentScrollWidth, submitButtonRect, lastSeatTop, checkoutBarRect, lastAvailableSeatRectBefore,
        focusedSeatId, expectedFocusedSeatId, focusedSeatRect,
        rights: { title: titleRect?.right ?? null, ticketTable: ticketTableRect?.right ?? null, countRow: countRowRect?.right ?? null, checkoutBar: checkoutBarRight },
        gridColumns: gridColumns.length, firstGridColumnWidth, infoColumnWidth: infoColumnRect?.width ?? null,
        purchaseColumnLeft: purchaseColumnRect?.left ?? null, infoColumnRight: infoColumnRect?.right ?? null,
        purchaseColumnTop: purchaseColumnRect?.top ?? null, infoColumnBottom: infoColumnRect?.bottom ?? null,
        quickPickButtonTop, zoneSelectTop,
        styles: { soldSeat: soldSeatStyle, availableSeat: availableSeatStyle, unavailableSwatch: unavailableSwatchStyle },
        assertion1, assertion2, assertion3, assertion4,
        valid: invalidReasons.length === 0, invalidReasons,
      };
    } finally {
      frame.remove();
    }
  }

  const results = [];
  for (const W of widths) {
    try {
      results.push(await measure(W));
    } catch (error) {
      results.push({ W, valid: false, invalidReasons: [`${error.name}: ${error.message}`] });
    }
  }
  return JSON.stringify(results);
})();
