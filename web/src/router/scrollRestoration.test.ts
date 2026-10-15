import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'
import { cancelPendingScrollRestoration, waitForRestorableScrollPosition } from './scrollRestoration'

const VIEWPORT_HEIGHT = 500
let contentHeight = 0

// jsdom 沒有版面配置，scrollHeight／clientHeight 恆為 0；以可變的 getter 模擬內容載入前後的頁面高度
function stubDocumentHeights(): void {
  Object.defineProperty(document.documentElement, 'scrollHeight', { configurable: true, get: () => contentHeight })
  Object.defineProperty(document.documentElement, 'clientHeight', { configurable: true, get: () => VIEWPORT_HEIGHT })
}

function setScrollableRange(range: number): void {
  contentHeight = VIEWPORT_HEIGHT + range
}

// 函式參照或 capture 選項不同時，瀏覽器不會移除 listener；只比對事件名稱抓不到這種殘留
function expectSameListenersRemoved(
  addEventListener: MockInstance<typeof window.addEventListener>,
  removeEventListener: MockInstance<typeof window.removeEventListener>,
): void {
  const toCapture = (options: boolean | EventListenerOptions | undefined): boolean =>
    typeof options === 'boolean' ? options : (options?.capture ?? false)
  const added = addEventListener.mock.calls.map(([eventName, listener, options]) => [eventName, listener, toCapture(options)])
  expect(added.map(([eventName]) => eventName)).toEqual(['wheel', 'touchmove', 'keydown'])
  const removed = removeEventListener.mock.calls.map(([eventName, listener, options]) => [eventName, listener, toCapture(options)])
  expect(removed).toEqual(expect.arrayContaining(added))
}

function trackSettlement<T>(promise: Promise<T>): { readonly isSettled: boolean; readonly value: T | undefined } {
  const state = { isSettled: false, value: undefined as T | undefined }
  void promise.then((value) => {
    state.isSettled = true
    state.value = value
  })
  return state
}

describe('waitForRestorableScrollPosition', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    stubDocumentHeights()
    setScrollableRange(0)
  })

  // 模組層級的 waiter 若留到下一個案例，會汙染 vi.getTimerCount() 的斷言
  afterEach(() => {
    cancelPendingScrollRestoration()
    vi.useRealTimers()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    delete (document.documentElement as unknown as Record<string, unknown>).scrollHeight
    delete (document.documentElement as unknown as Record<string, unknown>).clientHeight
  })

  // [BW-NAV-RESTORE-001] 導覽完成當下內容還沒載入；立即捲動會被截在當下的最低處，須等高度足夠才交回位置。
  it('[BW-NAV-RESTORE-001] waitForRestorableScrollPosition_WhenHeightBecomesEnough_ResolvesSavedPosition', async () => {
    const position = { left: 30, top: 900, behavior: 'instant' as const }
    const result = trackSettlement(waitForRestorableScrollPosition(position))

    // scrollHeight 1000 已超過 top 900，但可捲範圍只有 500；只看 scrollHeight 會提早捲動而被截在 500
    setScrollableRange(500)
    for (let frame = 0; frame < 5; frame++) vi.advanceTimersToNextFrame()
    await Promise.resolve()
    expect(result.isSettled).toBe(false)

    setScrollableRange(900)
    vi.advanceTimersToNextFrame()
    await Promise.resolve()

    expect(result.isSettled).toBe(true)
    // 同一個參照：left、behavior 原樣交給 vue-router，不重建也不刪減欄位
    expect(result.value).toBe(position)
    expect(result.value).toStrictEqual({ left: 30, top: 900, behavior: 'instant' })
    expect(vi.getTimerCount()).toBe(0)
  })

  // [BW-NAV-RESTORE-001] 高 DPI 下記錄位置帶小數；沒有 1px 容差時，停在最底端的頁面會白等 3 秒。
  it('[BW-NAV-RESTORE-001] waitForRestorableScrollPosition_WhenSavedAtFractionalBottom_ResolvesOnFirstFrame', async () => {
    setScrollableRange(1234)
    const position = { left: 0, top: 1234.5 }
    const result = trackSettlement(waitForRestorableScrollPosition(position))

    vi.advanceTimersToNextFrame()
    await Promise.resolve()

    expect(result.value).toBe(position)
  })

  // [BW-NAV-RESTORE-001] 容差邊界：可捲範圍剛好比記錄位置少 1px 仍算足夠，不能多等 3 秒逾時。
  it('[BW-NAV-RESTORE-001] waitForRestorableScrollPosition_WhenRangeIsExactlyOnePixelShort_ResolvesOnFirstFrame', async () => {
    setScrollableRange(899)
    const position = { left: 0, top: 900 }
    const result = trackSettlement(waitForRestorableScrollPosition(position))

    vi.advanceTimersToNextFrame()
    await Promise.resolve()

    expect(result.value).toBe(position)
  })

  // [BW-NAV-RESTORE-002] 回來時內容本身就比離開時短，永遠等不到足夠高度；不能一直等下去。
  it('[BW-NAV-RESTORE-002] waitForRestorableScrollPosition_WhenHeightNeverEnough_ResolvesSavedPositionAfter3Seconds', async () => {
    const addEventListener = vi.spyOn(window, 'addEventListener')
    const removeEventListener = vi.spyOn(window, 'removeEventListener')
    const position = { left: 0, top: 900 }
    const result = trackSettlement(waitForRestorableScrollPosition(position))

    vi.advanceTimersByTime(2999)
    await Promise.resolve()
    expect(result.isSettled).toBe(false)

    vi.advanceTimersByTime(1)
    await Promise.resolve()

    expect(result.value).toBe(position)
    expect(vi.getTimerCount()).toBe(0)
    expectSameListenersRemoved(addEventListener, removeEventListener)
  })

  // [BW-NAV-RESTORE-002] 背景分頁的 animation frame 會暫停；逾時若掛在 frame 上，等待會被無限延長。
  it('[BW-NAV-RESTORE-002] waitForRestorableScrollPosition_WhenFramesNeverRun_StillTimesOutAt3000ms', async () => {
    vi.useRealTimers()
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    vi.stubGlobal('requestAnimationFrame', vi.fn(() => 1))
    vi.stubGlobal('cancelAnimationFrame', vi.fn())
    const position = { left: 0, top: 900 }
    const result = trackSettlement(waitForRestorableScrollPosition(position))

    vi.advanceTimersByTime(3000)
    await Promise.resolve()

    expect(result.value).toBe(position)
  })

  // [BW-NAV-RESTORE-003] 使用者已自行捲動時，內容載入後再把畫面拉回舊位置會很突兀。
  it.each(['wheel', 'touchmove', 'keydown'])(
    '[BW-NAV-RESTORE-003] waitForRestorableScrollPosition_WhenUserScrollsWhileWaiting_ResolvesFalse (%s)',
    async (eventName) => {
      const addEventListener = vi.spyOn(window, 'addEventListener')
      const removeEventListener = vi.spyOn(window, 'removeEventListener')
      const result = trackSettlement(waitForRestorableScrollPosition({ left: 0, top: 900 }))

      window.dispatchEvent(new Event(eventName))
      await Promise.resolve()

      expect(result.isSettled).toBe(true)
      expect(result.value).toBe(false)
      expect(vi.getTimerCount()).toBe(0)
      expectSameListenersRemoved(addEventListener, removeEventListener)

      // 之後高度足夠或逾時都不能再改變結果
      setScrollableRange(900)
      vi.advanceTimersToNextFrame()
      vi.advanceTimersByTime(3000)
      await Promise.resolve()
      expect(result.value).toBe(false)
    },
  )

  // [BW-NAV-RESTORE-003] 函式本身須先放棄前一個等待（design 決策 3 第 4 點），不只靠 scrollBehavior 開頭的取消；
  // 否則前一個 waiter 失去參照，監聽與計時器會殘留到逾時，再 resolve 舊位置。
  it('[BW-NAV-RESTORE-003] waitForRestorableScrollPosition_WhenCalledAgainWhileWaiting_ResolvesPreviousFalse', async () => {
    const previous = trackSettlement(waitForRestorableScrollPosition({ left: 0, top: 900 }))
    const latestPosition = { left: 0, top: 400 }
    const latest = trackSettlement(waitForRestorableScrollPosition(latestPosition))
    await Promise.resolve()

    expect(previous.value).toBe(false)
    // 只剩最新等待的逾時與 animation frame
    expect(vi.getTimerCount()).toBe(2)

    setScrollableRange(900)
    vi.advanceTimersToNextFrame()
    await Promise.resolve()
    expect(latest.value).toBe(latestPosition)
  })

  it('[BW-NAV-RESTORE-003] cancelPendingScrollRestoration_WhileWaiting_ResolvesFalseAndCleansUp', async () => {
    const result = trackSettlement(waitForRestorableScrollPosition({ left: 0, top: 900 }))

    cancelPendingScrollRestoration()
    await Promise.resolve()

    expect(result.value).toBe(false)
    expect(vi.getTimerCount()).toBe(0)
  })
})
