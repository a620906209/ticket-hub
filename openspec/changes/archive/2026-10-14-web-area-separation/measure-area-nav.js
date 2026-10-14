// web-area-separation 版面量測腳本（design.md 決策 6、tasks.md 1.1）。以 admin-mobile-nav 的 measure-admin-nav.js 為基礎。
// 執行方式：使用者在瀏覽器以 Admin 登入並切換 Organizer 後，於前端 dev server 的同源分頁以 claude-in-chrome 的
// javascript_tool 執行本檔全文。
// - AREA：'organizer'（/admin/redeem、/admin/venues）、'platform'（/platform/organizers）、'buyer'（/）。
// - BASELINE = true：修改前的基準量測。'organizer' 的選單按鈕與 .admin-nav-logout 規則只輸出不判定；
//   areaSwitcher 只輸出不判定；'platform' 不量（修改前不存在）；layoutBackground／navBackground／navBorderTop
//   以修改後才存在的 CSS 變數為基準，基準時輸出 null 且不判定。BASELINE = false 時這三個欄位納入 pass 判定（AWU-AREA-LAYOUT-001）。
// - INJECT_LONG_TEXT：'organizer' 把主辦方名稱換成 100 字元無斷點字串；'buyer' 把會員名稱換成 30 字元字串（AWU-AREA-NAV-OVERFLOW-001）。
// - SELF_CHECK_BREAK_LINES：'buyer' 的單行判定自我檢查用，把品牌名稱、「活動」、「我的訂單」與會員名稱強制換行成一字一行，確認行數與頂列底緣判定都會推入 failReasons；正式量測維持 false。
// - SELF_CHECK_BREAK_TRUNCATION：'buyer' 的會員名稱截斷自我檢查用，把會員名稱改成 text-overflow: clip、line-height: 1，確認溢出與上下裁切判定都會推入 failReasons；
//   須搭配 INJECT_LONG_TEXT = true 且只量會被截斷的寬度（W ≤ 480）；正式量測維持 false。
// - ONLY_WIDTHS／SELF_CHECK_INJECT_WIDE：1.1 自我檢查用（限定寬度、在導覽根元素內注入 2000px 元素），正式量測維持 null／false。
// - SELF_CHECK_BREAK_COLORS：AWU-AREA-LAYOUT-001 自我檢查用，把版面根元素背景改成不透明的錯誤色、`--color-bg-elevated`
//   改成 alpha 為 0 的白色、色條寬度改 0、另一區背景變數改成與本區相同，確認各條判定都會推入 failReasons；正式量測維持 false。
// 回傳 JSON 字串（陣列），每筆對應一組（AREA、W、目標路徑、是否注入）。
(async () => {
  const AREA = 'organizer'; // 'organizer' | 'platform' | 'buyer'
  const BASELINE = false;
  const INJECT_LONG_TEXT = false;
  const ONLY_WIDTHS = null; // 例如 [320]
  const SELF_CHECK_INJECT_WIDE = false;
  const SELF_CHECK_BREAK_COLORS = false;
  const SELF_CHECK_BREAK_LINES = false;
  const SELF_CHECK_BREAK_TRUNCATION = false;

  const LONG_NAME = 'M'.repeat(100);
  const BUYER_LONG_NAME = '會'.repeat(15) + 'ABCDEFGHIJKLMNO';
  const AREA_CONFIG = {
    organizer: {
      paths: ['/admin/redeem', '/admin/venues'],
      widths: [320, 390, 720, 721, 800, 1280],
      navRoot: '.admin-nav',
      layoutRoot: '.admin-layout',
      layoutVariable: '--color-bg-organizer',
      accentVariable: '--color-area-organizer',
      requiredElements: {
        areaSwitcher: '.area-switcher',
        menuButton: '[aria-label="開啟後台選單"]',
        horizontalMenu: '.admin-nav-menu',
        topBarLogoutButton: '.admin-nav-logout',
        organizerIndicator: '.organizer-indicator',
      },
    },
    platform: {
      paths: ['/platform/organizers'],
      widths: [320, 390, 720, 721, 800, 1280],
      navRoot: '.platform-nav',
      layoutRoot: '.platform-layout',
      layoutVariable: '--color-bg-platform',
      accentVariable: '--color-area-platform',
      requiredElements: {
        areaSwitcher: '.area-switcher',
        platformMenu: '.platform-nav-menu',
        platformLogoutButton: '.platform-nav-logout',
      },
    },
    buyer: {
      paths: ['/'],
      // 361 是單行規則的起點、480／481 是縮寫斷點兩側；360 這側只量 320，因為 W ≤ 360 的要求本來就較寬鬆
      widths: [320, 361, 390, 480, 481, 1280],
      navRoot: '.buyer-header',
      layoutRoot: '.buyer-layout',
      layoutVariable: '--color-bg',
      accentVariable: null,
      requiredElements: {
        areaSwitcher: '.area-switcher',
        brand: '.brand',
        buyerNav: '.buyer-nav',
        buyerActions: '.buyer-actions',
        memberTrigger: '.member-trigger',
      },
    },
  };
  const config = AREA_CONFIG[AREA];
  if (!config) return JSON.stringify([{ AREA, valid: false, invalidReasons: [`未知的 AREA=${AREA}`] }]);
  if (BASELINE && AREA === 'platform') {
    return JSON.stringify([{ AREA, valid: false, invalidReasons: ['BASELINE 不量 platform（修改前不存在）'] }]);
  }
  const injectLongText = (AREA === 'organizer' || AREA === 'buyer') && INJECT_LONG_TEXT;
  const plans = (ONLY_WIDTHS ?? config.widths).flatMap((W) => config.paths.map((targetPath) => ({ W, targetPath })));

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

  async function measure({ W, targetPath }) {
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
      // SPA 要先以 refresh token 換發再渲染；之後再等主辦方名稱 API 回來，避免注入後被 Vue 重繪還原
      await waitForSelector(doc, config.navRoot, 15000);
      await sleep(2000);

      const invalidReasons = [];
      const failReasons = [];
      const navRoots = doc.querySelectorAll(config.navRoot);
      const layoutRoots = doc.querySelectorAll(config.layoutRoot);
      if (navRoots.length !== 1) invalidReasons.push(`${config.navRoot} 命中數=${navRoots.length}`);
      // 版面根元素找不到就是背景色沒套用（AREA-LAYOUT-001 失敗），不能當成無效量測跳過
      if (layoutRoots.length !== 1) failReasons.push(`${config.layoutRoot} 命中數=${layoutRoots.length}`);
      const nav = navRoots[0] ?? null;
      const layout = layoutRoots[0] ?? null;

      const indicatorsBeforeInject = nav ? [...nav.querySelectorAll('.organizer-indicator')] : [];
      if (injectLongText && AREA === 'organizer' && indicatorsBeforeInject.length === 1) indicatorsBeforeInject[0].textContent = LONG_NAME;
      const memberTriggersBeforeInject = nav ? [...nav.querySelectorAll('.member-trigger')] : [];
      if (injectLongText && AREA === 'buyer') {
        // 沒注入成功時量到的是一般名稱，不能當成長名稱通過
        if (memberTriggersBeforeInject.length === 1) memberTriggersBeforeInject[0].textContent = BUYER_LONG_NAME;
        else invalidReasons.push(`.member-trigger 命中數=${memberTriggersBeforeInject.length}，無法注入長名稱`);
      }
      if (SELF_CHECK_BREAK_LINES && AREA === 'buyer') {
        const style = doc.createElement('style');
        style.textContent = '.brand,.nav-link,.member-trigger{display:inline-block!important;width:1em!important;max-width:none!important;white-space:normal!important}';
        doc.head.appendChild(style);
      }
      if (SELF_CHECK_BREAK_TRUNCATION && AREA === 'buyer') {
        const style = doc.createElement('style');
        style.textContent = '.member-trigger{text-overflow:clip!important;line-height:1!important}';
        doc.head.appendChild(style);
      }
      if (SELF_CHECK_INJECT_WIDE && nav) {
        const wide = doc.createElement('div');
        wide.className = 'self-check-wide';
        wide.style.cssText = 'width:2000px;height:10px;flex:none';
        nav.appendChild(wide);
      }
      if (SELF_CHECK_BREAK_COLORS && layout && nav) {
        // 每種破壞各對應一條判定：不透明但不等於變數、變數本身 alpha 為 0（actual 與 expected 相同）、
        // 色條寬 0、另一區背景變數與本區相同
        layout.style.backgroundColor = 'rgb(1, 2, 3)';
        doc.documentElement.style.setProperty('--color-bg-elevated', '#ffffff00');
        nav.style.borderTopWidth = '0px';
        const otherArea = Object.keys(AREA_CONFIG).find((area) => area !== AREA);
        doc.documentElement.style.setProperty(AREA_CONFIG[otherArea].layoutVariable,
          win.getComputedStyle(doc.documentElement).getPropertyValue(config.layoutVariable).trim());
      }
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

      const navRootScrollWidth = nav ? track(nav.scrollWidth) : null;
      const navRootClientWidth = nav ? track(nav.clientWidth) : null;

      // 被截斷的主辦方名稱本來就以 overflow: hidden 截斷，不列入內容溢出檢查
      const measurable = nav
        ? [nav, ...nav.querySelectorAll('*')].filter((el) => el instanceof win.HTMLElement && isVisible(el))
        : [];
      if (nav && measurable.length === 0) invalidReasons.push(`${config.navRoot} 內可量測元素數為 0`);
      let navMaxRight = -Infinity;
      let navMaxRightElement = null;
      // 根元素自己的外框就是 W，navMaxRight 至少為 W；子孫的最大右緣才看得出新增元素前的餘裕（1.2）
      let descendantMaxRight = -Infinity;
      // 根元素的 scrollWidth 會含溢出子孫，failReasons 要能指出真正超出 W 的子孫
      const elementsBeyondW = [];
      const overflowingElements = [];
      for (const el of measurable) {
        const rect = el.getBoundingClientRect();
        track(rect.left); track(rect.width); track(rect.height); track(el.scrollWidth); track(el.clientWidth);
        // 截斷的主辦方名稱 scrollWidth 是完整文字寬度，超出外框的部分被裁掉看不到，右緣只取外框（admin-mobile-nav 4.2 紀錄）
        // 會員名稱同理，但只在確實以 overflow: hidden + ellipsis 截斷時豁免，避免截斷樣式失效時漏判溢出
        const memberTrigger = el.closest('.member-trigger');
        const memberStyle = memberTrigger ? win.getComputedStyle(memberTrigger) : null;
        const isTruncatedName = el.closest('.organizer-indicator') !== null
          || (memberStyle !== null && memberStyle.overflowX === 'hidden' && memberStyle.textOverflow === 'ellipsis');
        const right = Math.max(rect.right, isTruncatedName ? -Infinity : rect.left + el.scrollWidth);
        if (right > navMaxRight) { navMaxRight = right; navMaxRightElement = describeElement(el); }
        if (el !== nav) {
          descendantMaxRight = Math.max(descendantMaxRight, right);
          if (right > W) elementsBeyondW.push({ selector: describeElement(el), right });
        }
        if (el.scrollWidth > el.clientWidth && !isTruncatedName) {
          overflowingElements.push({ selector: describeElement(el), scrollWidth: el.scrollWidth, clientWidth: el.clientWidth });
        }
      }
      if (measurable.length === 0) navMaxRight = null;
      else track(navMaxRight);
      if (descendantMaxRight === -Infinity) descendantMaxRight = null;
      else track(descendantMaxRight);

      const elements = {};
      for (const [name, selector] of Object.entries(config.requiredElements)) {
        const matches = nav ? [...nav.querySelectorAll(selector)] : [];
        const rect = matches[0]?.getBoundingClientRect();
        elements[name] = {
          count: matches.length,
          visible: isVisible(matches[0]),
          rect: rect
            ? { left: track(rect.left), right: track(rect.right), width: track(rect.width), height: track(rect.height) }
            : null,
        };
      }
      if (elements.organizerIndicator) {
        const text = nav?.querySelector('.organizer-indicator')?.textContent.trim() ?? null;
        elements.organizerIndicator.text = text;
        elements.organizerIndicator.textLength = text?.length ?? null;
        // 注入後 Vue 若重繪會還原名稱，量到的就不是長名稱
        if (injectLongText && (elements.organizerIndicator.count !== 1 || elements.organizerIndicator.textLength !== 100)) {
          invalidReasons.push(`注入後主辦方名稱命中數=${elements.organizerIndicator.count}、文字長度=${elements.organizerIndicator.textLength}`);
        }
      }
      if (elements.memberTrigger) {
        const text = nav?.querySelector('.member-trigger')?.textContent.trim() ?? null;
        elements.memberTrigger.text = text;
        if (injectLongText && text !== BUYER_LONG_NAME) invalidReasons.push(`注入後會員名稱=${text}，不是 30 字元長名稱`);
      }

      // AWU-AREA-LAYOUT-001 欄位（7.2 判定）。CSS 變數先從 documentElement 讀出，再套到暫時元素取得瀏覽器的計算色
      const rootStyle = win.getComputedStyle(doc.documentElement);
      const resolveVariableColor = (variable) => {
        const raw = rootStyle.getPropertyValue(variable).trim();
        // 變數被刪除就是區域色失效，列為失敗而非無效
        if (raw === '') { failReasons.push(`CSS 變數 ${variable} 解析為空字串`); return null; }
        const probe = doc.createElement('div');
        probe.style.backgroundColor = raw;
        doc.body.appendChild(probe);
        const color = win.getComputedStyle(probe).backgroundColor;
        probe.remove();
        return color;
      };
      let layoutBackground = null;
      let navBackground = null;
      let navBorderTop = null;
      let areaLayoutBackgrounds = null;
      if (!BASELINE) {
        // 「三區背景兩兩不同」：每次只量一區，改為比對三個變數的計算色；配合各區 actual === expected，即推得三區實際背景兩兩不同
        areaLayoutBackgrounds = Object.fromEntries(Object.entries(AREA_CONFIG)
          .map(([area, areaConfig]) => [area, resolveVariableColor(areaConfig.layoutVariable)]));
        layoutBackground = {
          actual: layout ? win.getComputedStyle(layout).backgroundColor : null,
          variable: config.layoutVariable,
          expected: resolveVariableColor(config.layoutVariable),
        };
        if (config.accentVariable) {
          navBackground = {
            actual: nav ? win.getComputedStyle(nav).backgroundColor : null,
            variable: '--color-bg-elevated',
            expected: resolveVariableColor('--color-bg-elevated'),
          };
          navBorderTop = {
            width: nav ? win.getComputedStyle(nav).borderTopWidth : null,
            color: nav ? win.getComputedStyle(nav).borderTopColor : null,
            variable: config.accentVariable,
            expected: resolveVariableColor(config.accentVariable),
          };
        }
      }

      if (finiteNumbers.some((value) => !Number.isFinite(value))) invalidReasons.push('有量測值不是有限數值');
      const valid = invalidReasons.length === 0;

      // AC 判定（MOBILE-NAV-001、002、AREA-NAV-OVERFLOW-001）
      if (navMaxRight !== null && navMaxRight > W) {
        const culprits = elementsBeyondW.map((item) => `${item.selector}@${item.right}`).join(', ');
        failReasons.push(`navMaxRight=${navMaxRight} > W（${navMaxRightElement}；超出 W 的子孫：${culprits || '無'}）`);
      }
      if (overflowingElements.length > 0) failReasons.push(`overflowingElements: ${overflowingElements.map((item) => item.selector).join(', ')}`);
      if (navRootScrollWidth > navRootClientWidth || navRootScrollWidth > W) {
        failReasons.push(`navRootScrollWidth=${navRootScrollWidth}（clientWidth=${navRootClientWidth}、W=${W}）`);
      }
      for (const [name, element] of Object.entries(elements)) {
        if (BASELINE && name === 'areaSwitcher') continue;
        if (BASELINE && AREA === 'organizer' && (name === 'menuButton' || name === 'topBarLogoutButton')) continue;
        if (element.count !== 1) failReasons.push(`${name} 命中數=${element.count}`);
      }
      if (!BASELINE && !elements.areaSwitcher.visible) failReasons.push('areaSwitcher 不可見');
      // AC 判定（AREA-LAYOUT-001）
      if (!BASELINE) {
        // 瀏覽器把 alpha = 1 的計算色序列化為 rgb(...)，其他（含 #rrggbb00 這類 alpha 為 0 但 RGB 不為 0 的值）都不是不透明色；
        // 只比 actual === expected 擋不住變數本身就是透明色的情況
        const isOpaque = (color) => /^rgb\(\d+, \d+, \d+\)$/.test(color ?? '');
        const checkColor = (label, actual, variable, expected) => {
          if (!isOpaque(actual)) failReasons.push(`${label}=${actual} 不是不透明色`);
          if (actual !== expected) failReasons.push(`${label}=${actual} ≠ ${variable}（${expected}）`);
        };
        checkColor('layoutBackground', layoutBackground.actual, layoutBackground.variable, layoutBackground.expected);
        const distinctBackgrounds = new Set(Object.values(areaLayoutBackgrounds));
        if (distinctBackgrounds.size !== Object.keys(AREA_CONFIG).length) {
          failReasons.push(`三區背景色未兩兩不同：${JSON.stringify(areaLayoutBackgrounds)}`);
        }
        if (navBackground) {
          checkColor('navBackground', navBackground.actual, navBackground.variable, navBackground.expected);
          // spec 寫的是「背景為白色」，變數之後被改成非白色也要能發現
          if (navBackground.actual !== 'rgb(255, 255, 255)') failReasons.push(`navBackground=${navBackground.actual} 不是白色`);
        }
        if (navBorderTop) {
          if (navBorderTop.width !== '4px') failReasons.push(`navBorderTop 寬度=${navBorderTop.width} ≠ 4px`);
          checkColor('navBorderTop', navBorderTop.color, navBorderTop.variable, navBorderTop.expected);
        }
      }
      if (AREA === 'organizer') {
        const { menuButton, horizontalMenu, topBarLogoutButton, organizerIndicator } = elements;
        if (W <= 720) {
          if (!BASELINE && (!menuButton.visible || menuButton.rect.width < 44 || menuButton.rect.height < 44)) {
            failReasons.push(`menuButton 不可見或小於 44×44（${menuButton.rect?.width}×${menuButton.rect?.height}）`);
          }
          if (horizontalMenu.visible) failReasons.push('horizontalMenu 在 W ≤ 720 可見');
          if (!BASELINE && topBarLogoutButton.visible) failReasons.push('topBarLogoutButton 在 W ≤ 720 可見');
          if (targetPath === '/admin/redeem' && documentScrollWidth > W) failReasons.push(`documentScrollWidth=${documentScrollWidth} > W`);
        } else {
          if (!BASELINE && menuButton.visible) failReasons.push('menuButton 在 W ≥ 721 可見');
          if (!horizontalMenu.visible) failReasons.push('horizontalMenu 在 W ≥ 721 不可見');
          if (!BASELINE && !topBarLogoutButton.visible) failReasons.push('topBarLogoutButton 在 W ≥ 721 不可見');
        }
        if (!organizerIndicator.visible) failReasons.push('organizerIndicator 不可見');
      }
      if (AREA === 'platform') {
        if (!elements.platformMenu.visible) failReasons.push('platformMenu 不可見');
        if (!elements.platformLogoutButton.visible) failReasons.push('platformLogoutButton 不可見');
      }
      let lineCounts = null;
      let headerBottom = null;
      let descendantMaxBottom = null;
      let memberTriggerHeights = null;
      if (AREA === 'buyer') {
        for (const name of ['brand', 'buyerNav', 'buyerActions']) {
          if (!elements[name].visible) failReasons.push(`${name} 不可見`);
        }
        // 只看水平溢出抓不到「被擠成直排」：以文字範圍的行框頂端去重計算行數
        const countLines = (selector) => {
          const targets = nav ? nav.querySelectorAll(selector) : [];
          if (targets.length !== 1) return null;
          const range = doc.createRange();
          range.selectNodeContents(targets[0]);
          return new Set([...range.getClientRects()].map((rect) => Math.round(rect.top))).size;
        };
        lineCounts = {
          brand: countLines('.brand'),
          activityLink: countLines('.buyer-nav .nav-link'),
          ordersLink: countLines('.buyer-actions > .nav-link'),
          memberTrigger: countLines('.member-trigger'),
        };
        const singleLineRequired = W >= 361 ? Object.keys(lineCounts) : ['memberTrigger'];
        if (!BASELINE) {
          for (const name of singleLineRequired) {
            if (lineCounts[name] !== 1) failReasons.push(`${name} 行數=${lineCounts[name]}（W=${W} 須為單行）`);
          }
        }
        // 單行截斷用 overflow: hidden，行高不足時字母下伸部會被裁掉，水平溢出與行數都抓不到
        const memberTriggers = nav ? nav.querySelectorAll('.member-trigger') : [];
        if (memberTriggers.length === 1) {
          memberTriggerHeights = { clientHeight: memberTriggers[0].clientHeight, scrollHeight: memberTriggers[0].scrollHeight };
          if (!BASELINE && memberTriggerHeights.scrollHeight > memberTriggerHeights.clientHeight) {
            failReasons.push(`會員名稱上下被裁切（scrollHeight=${memberTriggerHeights.scrollHeight} > clientHeight=${memberTriggerHeights.clientHeight}）`);
          }
        }
        if (nav) {
          headerBottom = nav.getBoundingClientRect().bottom;
          const bottoms = [...nav.querySelectorAll('*')].filter((el) => el.getClientRects().length > 0)
            .map((el) => el.getBoundingClientRect().bottom);
          descendantMaxBottom = bottoms.length ? Math.max(...bottoms) : null;
          if (!BASELINE && descendantMaxBottom > headerBottom) {
            failReasons.push(`頂列內元素底緣=${descendantMaxBottom} > 頂列底緣=${headerBottom}`);
          }
        }
      }

      return {
        AREA, W, targetPath, injectLongText, baseline: BASELINE, selfCheckInjectWide: SELF_CHECK_INJECT_WIDE, selfCheckBreakColors: SELF_CHECK_BREAK_COLORS,
        selfCheckBreakLines: SELF_CHECK_BREAK_LINES, selfCheckBreakTruncation: SELF_CHECK_BREAK_TRUNCATION,
        pathname, clientWidth, documentScrollWidth, navRootScrollWidth, navRootClientWidth, navMaxRight, navMaxRightElement,
        descendantMaxRight, elementsBeyondW, overflowingElements, elements, layoutBackground, navBackground, navBorderTop, areaLayoutBackgrounds,
        lineCounts, memberTriggerHeights, headerBottom, descendantMaxBottom,
        valid, invalidReasons, pass: valid && failReasons.length === 0, failReasons,
      };
    } finally {
      frame.remove();
    }
  }

  const results = [];
  for (const plan of plans) results.push(await measure(plan));
  return JSON.stringify(results);
})();
