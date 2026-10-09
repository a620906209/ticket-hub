// admin-mobile-nav 版面量測腳本（design.md 決策 5、tasks.md 1.1）。
// 執行方式：使用者在瀏覽器登入後，於前端 dev server 的同源分頁以 claude-in-chrome 的 javascript_tool 執行本檔全文。
// - ORGANIZER_CONTEXT = 'unswitched'：登入後尚未切換 Organizer，只量 /admin/organizers（W = 320、390）；
//   此模式不注入長名稱，因為要先確認頂列顯示「尚未切換主辦方」。
// - ORGANIZER_CONTEXT = 'switched'：已切換到 Approved Organizer，量 /admin/redeem 與 /admin/venues（全部寬度）。
// - BASELINE = true：修改前的基準量測，選單按鈕與 .admin-nav-logout 尚未存在，不檢查兩者的命中數。
// 回傳 JSON 字串（陣列），每筆對應一組（W、目標路徑、是否注入）。
(async () => {
  const ORGANIZER_CONTEXT = 'switched'; // 'switched' | 'unswitched'
  const INJECT_LONG_TEXT = false;
  const BASELINE = false;

  const UNSWITCHED_INDICATOR_TEXT = '尚未切換主辦方';
  const LONG_NAME = 'M'.repeat(100);
  const MENU_BUTTON_SELECTOR = '[aria-label="開啟後台選單"]';
  const plans = ORGANIZER_CONTEXT === 'unswitched'
    ? [320, 390].map((W) => ({ W, targetPath: '/admin/organizers', injectLongText: false }))
    : [320, 390, 720, 721, 800, 1280].flatMap((W) => ['/admin/redeem', '/admin/venues']
      .map((targetPath) => ({ W, targetPath, injectLongText: INJECT_LONG_TEXT })));

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

  function describeElement(el) {
    const classes = [...el.classList].map((name) => `.${name}`).join('');
    return `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ''}${classes}`;
  }

  async function measure({ W, targetPath, injectLongText }) {
    const frame = document.createElement('iframe');
    frame.style.cssText = `position:fixed;top:0;left:0;width:${W}px;height:900px;border:0;z-index:99999;background:#fff`;
    frame.src = targetPath;
    document.body.appendChild(frame);
    try {
      await new Promise((resolve) => frame.addEventListener('load', resolve, { once: true }));
      const win = frame.contentWindow;
      const doc = frame.contentDocument;
      // 量測基準一律是 W：隱藏 iframe 捲軸（仍可捲動），模擬手機覆蓋式捲軸不佔寬度；clientWidth ≠ W 時判定無效
      doc.documentElement.style.scrollbarWidth = 'none';
      // SPA 要先以 refresh token 換發再渲染後台；之後再等主辦方名稱 API 回來，避免注入後被 Vue 重繪還原
      await waitForSelector(doc, '.admin-nav', 15000);
      await sleep(2000);

      const invalidReasons = [];
      const indicators = [...doc.querySelectorAll('.admin-nav .organizer-indicator')];
      if (injectLongText && indicators.length === 1) indicators[0].textContent = LONG_NAME;
      // 等 el-menu 的 ResizeObserver 與版面重算
      await sleep(300);

      const isVisible = (el) => {
        if (!el) return false;
        const style = win.getComputedStyle(el);
        const rect = el.getBoundingClientRect();
        return style.display !== 'none' && style.visibility !== 'hidden' && rect.width > 0 && rect.height > 0;
      };
      const finiteNumbers = [];
      const track = (value) => { finiteNumbers.push(value); return value; };

      const pathname = win.location.pathname;
      const clientWidth = track(doc.documentElement.clientWidth);
      const documentScrollWidth = track(doc.documentElement.scrollWidth);
      if (clientWidth !== W) invalidReasons.push(`clientWidth=${clientWidth} ≠ W`);
      if (pathname !== targetPath) invalidReasons.push(`pathname=${pathname} ≠ ${targetPath}`);

      const navs = doc.querySelectorAll('.admin-nav');
      if (navs.length !== 1) invalidReasons.push(`.admin-nav 命中數=${navs.length}`);
      const nav = navs[0] ?? null;

      // 被截斷的主辦方名稱本來就以 overflow: hidden 截斷，不列入內容溢出檢查
      const measurable = nav
        ? [nav, ...nav.querySelectorAll('*')].filter((el) => el instanceof win.HTMLElement && isVisible(el))
        : [];
      if (measurable.length === 0) invalidReasons.push('.admin-nav 內可量測元素數為 0');
      let navMaxRight = -Infinity;
      const overflowingElements = [];
      for (const el of measurable) {
        const rect = el.getBoundingClientRect();
        track(rect.left); track(rect.width); track(rect.height); track(el.scrollWidth); track(el.clientWidth);
        // 截斷的主辦方名稱 scrollWidth 是完整文字寬度，超出外框的部分被 overflow: hidden 裁掉看不到，
        // 右緣只取外框（spec 豁免截斷名稱的內容寬度）；其他元素以 left + scrollWidth 計算
        const isTruncatedName = el.closest('.organizer-indicator') !== null;
        navMaxRight = Math.max(navMaxRight, rect.right, isTruncatedName ? -Infinity : rect.left + el.scrollWidth);
        if (el.scrollWidth > el.clientWidth && !isTruncatedName) {
          overflowingElements.push({ selector: describeElement(el), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth });
        }
      }
      if (measurable.length === 0) navMaxRight = null;
      else track(navMaxRight);

      const menuButtons = nav ? [...nav.querySelectorAll(MENU_BUTTON_SELECTOR)] : [];
      const menuButtonRect = menuButtons[0]?.getBoundingClientRect();
      const menuButton = {
        count: menuButtons.length,
        visible: isVisible(menuButtons[0]),
        width: menuButtonRect ? track(menuButtonRect.width) : null,
        height: menuButtonRect ? track(menuButtonRect.height) : null,
      };
      if (!BASELINE && menuButton.count !== 1) invalidReasons.push(`選單按鈕命中數=${menuButton.count}`);

      const horizontalMenus = nav ? [...nav.querySelectorAll('.admin-nav-menu')] : [];
      const horizontalMenu = {
        count: horizontalMenus.length,
        display: horizontalMenus[0] ? win.getComputedStyle(horizontalMenus[0]).display : null,
      };
      if (horizontalMenu.count !== 1) invalidReasons.push(`.admin-nav-menu 命中數=${horizontalMenu.count}`);

      const logoutButtons = nav ? [...nav.querySelectorAll('.admin-nav-logout')] : [];
      const topBarLogoutButton = {
        count: logoutButtons.length,
        display: logoutButtons[0] ? win.getComputedStyle(logoutButtons[0]).display : null,
      };
      if (!BASELINE && topBarLogoutButton.count !== 1) invalidReasons.push(`.admin-nav-logout 命中數=${topBarLogoutButton.count}`);

      const indicatorText = indicators[0]?.textContent.trim() ?? null;
      const organizerIndicator = {
        count: indicators.length,
        visible: isVisible(indicators[0]),
        text: indicatorText,
        textLength: indicatorText?.length ?? null,
      };
      if (organizerIndicator.count !== 1) invalidReasons.push(`.organizer-indicator 命中數=${organizerIndicator.count}`);
      if (!organizerIndicator.visible) invalidReasons.push('.organizer-indicator 不可見');
      if (ORGANIZER_CONTEXT === 'unswitched' && indicatorText !== UNSWITCHED_INDICATOR_TEXT) {
        invalidReasons.push(`未切換狀態的主辦方名稱文字=${indicatorText} ≠ ${UNSWITCHED_INDICATOR_TEXT}`);
      }
      // 注入後 Vue 若重繪會還原名稱，量到的就不是長名稱
      if (injectLongText && (organizerIndicator.count !== 1 || organizerIndicator.textLength !== 100)) {
        invalidReasons.push(`注入後主辦方名稱命中數=${organizerIndicator.count}、文字長度=${organizerIndicator.textLength}`);
      }

      if (finiteNumbers.some((value) => !Number.isFinite(value))) invalidReasons.push('有量測值不是有限數值');
      if (menuButton.visible && (menuButton.width <= 0 || menuButton.height <= 0)) invalidReasons.push('選單按鈕可見但寬高 ≤ 0');

      return {
        W, targetPath, injectLongText, baseline: BASELINE, pathname, clientWidth, navMaxRight,
        overflowingElements, documentScrollWidth, menuButton, horizontalMenu, topBarLogoutButton, organizerIndicator,
        valid: invalidReasons.length === 0, invalidReasons,
      };
    } finally {
      frame.remove();
    }
  }

  const results = [];
  for (const plan of plans) results.push(await measure(plan));
  return JSON.stringify(results);
})();
