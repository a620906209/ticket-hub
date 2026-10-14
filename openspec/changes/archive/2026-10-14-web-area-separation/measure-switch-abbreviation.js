// AWU-AREA-SWITCH-006 瀏覽器確認（可見文字部分，例外見 design.md「AC 測試對應例外」）
// 用法：以任一已登入帳號開啟前端任一頁，把整段貼到 DevTools Console 執行；分頁需在前景。
// 以 iframe 開啟 `/`，寬度為目標 W，並隱藏 iframe 捲軸，讓 media query 的寬度等於 W；clientWidth ≠ W 時判定無效。
// SELF_CHECK_FORCE_FULL_LABEL = true 時，在 iframe 內注入樣式強制顯示完整名稱、隱藏縮寫，確認 W = 320、390、480 會判為不通過；正式量測維持 false。
(async () => {
  const SELF_CHECK_FORCE_FULL_LABEL = false;
  // 480／481 是縮寫斷點兩側，斷點被改動時才會失敗
  const EXPECTED = { 320: '前台', 390: '前台', 480: '前台', 481: '會員前台', 1280: '會員前台' };
  const results = [];
  for (const W of Object.keys(EXPECTED).map(Number)) {
    const frame = document.createElement('iframe');
    frame.style.cssText = `position:fixed;left:0;top:0;width:${W}px;height:600px;border:0;z-index:99999`;
    frame.src = '/';
    document.body.appendChild(frame);
    try {
      await new Promise((resolve) => (frame.onload = resolve));
      const doc = frame.contentDocument;
      // 不能用加寬 iframe 補捲軸：media query 寬度含捲軸，會變成 W + 捲軸寬，480 會被量成約 495（8.1 第 4 輪）
      doc.documentElement.style.scrollbarWidth = 'none';
      await new Promise((resolve) => setTimeout(resolve, 3000));
      if (SELF_CHECK_FORCE_FULL_LABEL) {
        const style = doc.createElement('style');
        style.textContent = '.area-switcher-label{display:inline!important}.area-switcher-label-short{display:none!important}';
        doc.head.appendChild(style);
      }
      const isVisible = (element) => !!element && element.getClientRects().length > 0;
      const triggers = doc.querySelectorAll('.buyer-header .area-switcher-trigger');
      const trigger = triggers[0];
      const header = doc.querySelector('.buyer-header');
      const descendants = header ? [...header.querySelectorAll('*')].filter(isVisible).map((e) => e.getBoundingClientRect()) : [];
      const record = {
        W,
        pathname: frame.contentWindow.location.pathname,
        clientWidth: doc.documentElement.clientWidth,
        triggerCount: triggers.length,
        ariaLabel: trigger?.getAttribute('aria-label') ?? null,
        title: trigger?.getAttribute('title') ?? null,
        // innerText 只含實際顯示的文字，按鈕內不在 span 裡的文字節點也會算進去
        visibleText: trigger ? trigger.innerText.replace(/\s+/g, '') : null,
        headerBottom: header?.getBoundingClientRect().bottom ?? null,
        descendantMaxBottom: descendants.length ? Math.max(...descendants.map((r) => r.bottom)) : null,
      };
      // 切換鈕不存在是 SWITCH-006 的失敗而非量測無效，triggerCount 放在 pass
      record.valid = record.clientWidth === W && record.pathname === '/';
      record.pass = record.valid
        && record.triggerCount === 1
        && record.ariaLabel === '會員前台'
        && record.title === '會員前台'
        && record.visibleText === EXPECTED[W]
        // 頂列內元素不得超出頂列（4.4 要修的就是「我的訂單」直排後超出 56px 頂列）
        && record.descendantMaxBottom <= record.headerBottom;
      results.push(record);
    } finally {
      frame.remove();
    }
  }
  const json = JSON.stringify(results, null, 2);
  console.log(json);
  return json;
})();
