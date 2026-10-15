// 上一頁／下一頁回到資料非同步載入的頁面時，導覽完成當下內容還沒渲染，直接捲到 savedPosition 會被截在
// 當下的最低處；改為等頁面高度足夠（或逾時）才交回位置（frontend-minor-polish design.md 決策 3）。

const RESTORE_TIMEOUT_MS = 3000
// 高 DPI 下記錄的 scrollY 可能帶小數，scrollHeight 是整數；沒有容差的話離開時位於最底端的頁面會白等到逾時
const HEIGHT_TOLERANCE_PX = 1
const USER_SCROLL_EVENTS = ['wheel', 'touchmove', 'keydown'] as const
const LISTENER_OPTIONS: AddEventListenerOptions = { passive: true }

let cancelPendingWaiter: (() => void) | null = null

function hasRestorableHeight(top: number): boolean {
  const root = document.documentElement
  // 用 clientHeight 而非 innerHeight：innerHeight 含橫向捲軸高度，算出的可捲範圍會偏小
  return root.scrollHeight - root.clientHeight >= top - HEIGHT_TOLERANCE_PX
}

/** 放棄目前等待中的還原（以 false resolve），由路由在每次導覽完成時呼叫。 */
export function cancelPendingScrollRestoration(): void {
  cancelPendingWaiter?.()
}

/**
 * 等到頁面高度足以捲到 position.top 時 resolve 原本的 position 物件；3 秒逾時同樣 resolve position，
 * 交由瀏覽器停在可捲到的最低處。等待期間使用者自行捲動或被新的等待取代時 resolve false，不再捲動。
 */
export function waitForRestorableScrollPosition<T extends { top: number }>(position: T): Promise<T | false> {
  cancelPendingScrollRestoration()

  return new Promise((resolve) => {
    // 每次等待時才讀取，測試替換的 stub 才會生效
    const requestFrame = window.requestAnimationFrame.bind(window)
    const cancelFrame = window.cancelAnimationFrame.bind(window)
    let frameId = 0

    const settle = (result: T | false): void => {
      for (const eventName of USER_SCROLL_EVENTS) window.removeEventListener(eventName, abort, LISTENER_OPTIONS)
      clearTimeout(timeoutId)
      cancelFrame(frameId)
      if (cancelPendingWaiter === abort) cancelPendingWaiter = null
      resolve(result)
    }
    function abort(): void {
      settle(false)
    }
    const checkHeight = (): void => {
      if (hasRestorableHeight(position.top)) {
        settle(position)
        return
      }
      frameId = requestFrame(checkHeight)
    }

    // 逾時不掛在 animation frame 上：背景分頁或裝置忙碌時 frame 會暫停，不應因此延長等待
    const timeoutId = setTimeout(() => settle(position), RESTORE_TIMEOUT_MS)
    for (const eventName of USER_SCROLL_EVENTS) window.addEventListener(eventName, abort, LISTENER_OPTIONS)
    cancelPendingWaiter = abort
    frameId = requestFrame(checkHeight)
  })
}
