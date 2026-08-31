import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { performRedemption } from '../utils/ticketRedemptionOutcome'
import { useRedemptionScanner, type RedemptionScannerDeps } from './useRedemptionScanner'

vi.mock('../utils/ticketRedemptionOutcome')

const VALID_GUID = '3fa85f64-5717-4562-b3fc-2c963f66afa6'
const QR_CONTENT = `${VALID_GUID}.the-signature`
const FAKE_STREAM = {} as MediaStream
const FAKE_IMAGE_DATA = {} as ImageData

function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

// requestFrame/cancelFrame 的假實作：讓測試能手動觸發「下一影格」，不依賴真實 rAF 時間軸
// （design.md Risks）。decodeTick 每次執行都會先重新 requestFrame 自己，tick() 呼叫的就是
// 最後一次註冊的 callback。
function createFrameController() {
  let nextId = 1
  let pending: ((timestamp: number) => void) | null = null
  const requestFrame = vi.fn((callback: (timestamp: number) => void) => {
    pending = callback
    return nextId++
  })
  const cancelFrame = vi.fn(() => {
    pending = null
  })
  function tick(timestamp = 0): void {
    const callback = pending
    if (!callback) throw new Error('沒有已註冊的 frame callback 可觸發')
    callback(timestamp)
  }
  return { requestFrame, cancelFrame, tick, hasPending: () => pending !== null }
}

function createScanner(overrides: Partial<RedemptionScannerDeps> = {}) {
  const frames = createFrameController()
  const openCameraStream = vi.fn().mockResolvedValue(FAKE_STREAM)
  const stopCameraStream = vi.fn()
  const deps: Partial<RedemptionScannerDeps> = {
    isCameraCapable: () => true,
    openCameraStream,
    stopCameraStream,
    classifyCameraError: () => 'error',
    requestFrame: frames.requestFrame,
    cancelFrame: frames.cancelFrame,
    isFrameReady: () => true,
    readFrame: () => FAKE_IMAGE_DATA,
    decodeQrFromImageData: () => null,
    shouldDecodeNow: () => true,
    ...overrides,
  }
  const scanner = useRedemptionScanner(deps)
  return { scanner, frames, openCameraStream, stopCameraStream }
}

beforeEach(() => {
  vi.mocked(performRedemption).mockReset()
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('相機初始化與能力偵測（決策 1／決策 4）', () => {
  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED
  it('不支援時直接進入 unsupported，不嘗試呼叫 getUserMedia', async () => {
    const { scanner, openCameraStream } = createScanner({ isCameraCapable: () => false })

    scanner.mount()
    await Promise.resolve()

    expect(scanner.state.value).toBe('unsupported')
    expect(openCameraStream).not.toHaveBeenCalled()
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE
  it.each([
    ['permission-denied', 'permission-denied'],
    ['camera-unavailable', 'camera-unavailable'],
    ['error', 'error'],
  ] as const)('getUserMedia 例外分類為 %s 時進入對應狀態', async (classified, expected) => {
    const { scanner } = createScanner({
      openCameraStream: vi.fn().mockRejectedValue(new Error('boom')),
      classifyCameraError: () => classified,
    })

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()

    expect(scanner.state.value).toBe(expected)
  })

  it('成功取得相機後進入 scanning', async () => {
    const { scanner } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()

    expect(scanner.state.value).toBe('scanning')
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-RETRY-CAMERA-STILL-FAILS
  it('重新嘗試相機後以新的失敗原因更新狀態，不卡在載入中畫面', async () => {
    const openCameraStream = vi
      .fn()
      .mockRejectedValueOnce(new Error('no camera'))
      .mockRejectedValueOnce(new Error('denied'))
    const classifyCameraError = vi.fn().mockReturnValueOnce('camera-unavailable').mockReturnValueOnce('permission-denied')
    const { scanner } = createScanner({ openCameraStream, classifyCameraError })

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('camera-unavailable')

    scanner.retryCamera()
    await Promise.resolve()
    await Promise.resolve()

    expect(scanner.state.value).toBe('permission-denied')
  })
})

describe('背景/前景切換與 race condition 保護（決策 4）', () => {
  // 對應 AC: ADMIN-REDEEM-BACKGROUND-PROCESSING-COMPLETES
  it('hidden 時停止偵測迴圈並釋放 stream track；visible 時 processing 不重送核銷請求', async () => {
    const { promise: redeemPromise, resolve } = createDeferred<{ kind: 'success' }>()
    vi.mocked(performRedemption).mockReturnValue(redeemPromise as ReturnType<typeof performRedemption>)
    const { scanner, frames, stopCameraStream } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleDetectedContent(QR_CONTENT)
    expect(scanner.state.value).toBe('processing')

    scanner.handleHidden()
    expect(stopCameraStream).toHaveBeenCalledWith(FAKE_STREAM)
    expect(frames.hasPending()).toBe(false)

    scanner.handleVisible()
    expect(performRedemption).toHaveBeenCalledTimes(1) // 不重新發送核銷請求

    resolve({ kind: 'success' })
    await Promise.resolve()
    await Promise.resolve()

    expect(scanner.state.value).toBe('result')
    expect(scanner.scanResult.value).toBe('success')
  })

  // 對應 AC: ADMIN-REDEEM-BACKGROUND-PROCESSING-COMPLETES（核銷請求「在背景時」完成，
  // 而不是像上一個測試那樣先 visible 才 resolve——這裡要驗證 document.hidden 為 true
  // 期間 resolve 時，不會提前顯示結果、啟動倒數、或在背景中重新呼叫相機）
  it('核銷請求在背景時（document.hidden 為 true）完成，不提前顯示結果或啟動倒數，直到切回前景才顯示', async () => {
    const { promise: redeemPromise, resolve } = createDeferred<{ kind: 'not-found' }>()
    vi.mocked(performRedemption).mockReturnValue(redeemPromise as ReturnType<typeof performRedemption>)
    const { scanner, openCameraStream } = createScanner()
    const hiddenSpy = vi.spyOn(document, 'hidden', 'get')

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleDetectedContent(QR_CONTENT)
    expect(scanner.state.value).toBe('processing')

    hiddenSpy.mockReturnValue(true)
    scanner.handleHidden()
    const callsBeforeResolve = openCameraStream.mock.calls.length

    // 核銷請求在背景時完成
    resolve({ kind: 'not-found' })
    await Promise.resolve()
    await Promise.resolve()

    // 還在背景：不得提前顯示結果、不得啟動倒數計時、不得在背景中重新呼叫相機
    expect(scanner.state.value).toBe('processing')
    expect(scanner.scanResult.value).toBeNull()
    expect(openCameraStream.mock.calls.length).toBe(callsBeforeResolve)

    // 切回前景，這時候才真正顯示結果並開始倒數
    hiddenSpy.mockReturnValue(false)
    scanner.handleVisible()

    expect(scanner.state.value).toBe('result')
    expect(scanner.scanResult.value).toBe('not-found')

    hiddenSpy.mockRestore()
  })

  // 對應 AC: ADMIN-REDEEM-BACKGROUND-RESUME
  it('切背景前為 scanning，visible 時重新初始化相機（重新呼叫 getUserMedia）', async () => {
    const { scanner, openCameraStream } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('scanning')
    expect(openCameraStream).toHaveBeenCalledTimes(1)

    scanner.handleHidden()
    scanner.handleVisible()
    await Promise.resolve()
    await Promise.resolve()

    expect(openCameraStream).toHaveBeenCalledTimes(2)
    expect(scanner.state.value).toBe('scanning')
  })

  it('切背景前為 result，visible 時保留結果內容並重啟倒數，不立即重新初始化相機', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'not-found' })
    const { scanner, openCameraStream } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleDetectedContent(QR_CONTENT)
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('result')
    const callsBeforeHidden = openCameraStream.mock.calls.length

    scanner.handleHidden()
    scanner.handleVisible()

    expect(scanner.state.value).toBe('result')
    expect(scanner.scanResult.value).toBe('not-found')
    expect(openCameraStream.mock.calls.length).toBe(callsBeforeHidden) // 結果顯示完才重新初始化，不是立即

    vi.advanceTimersByTime(4000)
    await Promise.resolve()
    await Promise.resolve()

    expect(scanner.state.value).toBe('scanning')
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE（不自動重試）
  it.each(['permission-denied', 'camera-unavailable', 'error'] as const)(
    '切背景前為 %s，visible 時 MUST NOT 自動重試',
    async (failedState) => {
      const { scanner, openCameraStream } = createScanner({
        openCameraStream: vi.fn().mockRejectedValue(new Error('boom')),
        classifyCameraError: () => failedState,
      })

      scanner.mount()
      await Promise.resolve()
      await Promise.resolve()
      expect(scanner.state.value).toBe(failedState)
      const callsBefore = openCameraStream.mock.calls.length

      scanner.handleHidden()
      scanner.handleVisible()

      expect(scanner.state.value).toBe(failedState)
      expect(openCameraStream.mock.calls.length).toBe(callsBefore)
    },
  )

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED（不自動重試）
  it('切背景前為 unsupported，visible 時 MUST NOT 自動重試', async () => {
    const { scanner, openCameraStream } = createScanner({ isCameraCapable: () => false })

    scanner.mount()
    await Promise.resolve()
    expect(scanner.state.value).toBe('unsupported')

    scanner.handleHidden()
    scanner.handleVisible()

    expect(scanner.state.value).toBe('unsupported')
    expect(openCameraStream).not.toHaveBeenCalled()
  })

  it('元件已 unmount 後收到 visible 通知不會重新初始化', async () => {
    const { scanner, openCameraStream } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleHidden()
    scanner.unmount()
    const callsBefore = openCameraStream.mock.calls.length

    scanner.handleVisible()

    expect(openCameraStream.mock.calls.length).toBe(callsBefore)
  })

  // 對應 design.md 決策 4 的 race condition 保護
  it('getUserMedia() 尚未 resolve 前觸發 hidden，稍後 resolve 時立即停止 stream、不掛載也不啟動迴圈', async () => {
    const { promise, resolve } = createDeferred<MediaStream>()
    const stopCameraStream = vi.fn()
    const { scanner, frames } = createScanner({
      openCameraStream: vi.fn().mockReturnValue(promise),
      stopCameraStream,
    })

    scanner.mount()
    expect(scanner.state.value).toBe('initializing')

    scanner.handleHidden()
    resolve(FAKE_STREAM)
    await Promise.resolve()
    await Promise.resolve()

    expect(stopCameraStream).toHaveBeenCalledWith(FAKE_STREAM)
    expect(scanner.state.value).not.toBe('scanning')
    expect(frames.hasPending()).toBe(false)
  })

  it('getUserMedia() 尚未 resolve 前元件 unmount，稍後 resolve 時不更新任何狀態', async () => {
    const { promise, resolve } = createDeferred<MediaStream>()
    const stopCameraStream = vi.fn()
    const { scanner } = createScanner({
      openCameraStream: vi.fn().mockReturnValue(promise),
      stopCameraStream,
    })

    scanner.mount()
    scanner.unmount()
    const stateAtUnmount = scanner.state.value

    resolve(FAKE_STREAM)
    await Promise.resolve()
    await Promise.resolve()

    expect(stopCameraStream).toHaveBeenCalledWith(FAKE_STREAM)
    expect(scanner.state.value).toBe(stateAtUnmount)
  })
})

describe('當輪 dedupe（決策 7）', () => {
  // 對應 AC: ADMIN-REDEEM-SCAN-DEDUPE
  it('result 顯示期間持續回報相同內容，redeemTicket 只被呼叫一次', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'success' })
    const { scanner } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()

    scanner.handleDetectedContent(QR_CONTENT)
    scanner.handleDetectedContent(QR_CONTENT) // 殘影：processing 期間持續回報相同內容
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleDetectedContent(QR_CONTENT) // result 顯示期間持續回報相同內容

    expect(performRedemption).toHaveBeenCalledTimes(1)
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-RETRY-AFTER-ERROR
  it('系統錯誤後恢復 scanning，再次掃到相同內容會重新呼叫（不被永久忽略）', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'system-error' })
    const { scanner } = createScanner()

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleDetectedContent(QR_CONTENT)
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('result')

    scanner.dismissResult() // 立即繼續掃描
    expect(scanner.state.value).toBe('scanning')

    scanner.handleDetectedContent(QR_CONTENT)

    expect(performRedemption).toHaveBeenCalledTimes(2)
  })
})

describe('手動輸入核銷（決策 5／決策 6）', () => {
  it.each([
    ['success', 'success'],
    ['already-redeemed', 'already-redeemed'],
    ['not-found', 'not-found'],
    ['system-error', 'system-error'],
  ] as const)('%s 情境：signature 固定傳 null，結果顯示為 %s', async (outcomeKind, expectedResult) => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: outcomeKind })
    const { scanner } = createScanner({ isCameraCapable: () => false })
    scanner.mount()
    await Promise.resolve()

    const submitted = scanner.submitManualRedemption(VALID_GUID)
    await submitted
    await Promise.resolve()

    expect(performRedemption).toHaveBeenCalledWith(VALID_GUID, null)
    expect(scanner.scanResult.value).toBe(expectedResult)
    expect(scanner.state.value).toBe('result')
  })

  it('格式不正確時不呼叫 API，回傳 formatValid: false', async () => {
    const { scanner } = createScanner({ isCameraCapable: () => false })
    scanner.mount()
    await Promise.resolve()

    const result = await scanner.submitManualRedemption('not-a-guid')

    expect(result).toEqual({ formatValid: false })
    expect(performRedemption).not.toHaveBeenCalled()
  })

  // 對應決策 4：手動輸入是在相機不可用的狀態下進行，結果顯示完不得誤觸自動重試相機
  it('相機本就不可用時，手動核銷完成後結果顯示完維持原本的不可用狀態，不自動重試相機', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'success' })
    const openCameraStream = vi.fn().mockRejectedValue(new Error('boom'))
    const { scanner } = createScanner({ openCameraStream, classifyCameraError: () => 'camera-unavailable' })
    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('camera-unavailable')
    const callsBefore = openCameraStream.mock.calls.length

    await scanner.submitManualRedemption(VALID_GUID)
    vi.advanceTimersByTime(1500)
    await Promise.resolve()

    expect(scanner.state.value).toBe('camera-unavailable')
    expect(openCameraStream.mock.calls.length).toBe(callsBefore)
  })
})

describe('結果顯示停留時間與恢復（決策 3）', () => {
  // 對應 AC: ADMIN-REDEEM-SCAN-AUTO-RESUME
  it('成功結果 1.5 秒後自動恢復可掃描狀態', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'success' })
    const { scanner } = createScanner()
    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()

    scanner.handleDetectedContent(QR_CONTENT)
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('result')

    vi.advanceTimersByTime(1499)
    expect(scanner.state.value).toBe('result')
    vi.advanceTimersByTime(1)
    await Promise.resolve()

    expect(scanner.state.value).toBe('scanning')
  })

  // 對應 AC: ADMIN-REDEEM-SCAN-AUTO-RESUME
  it('錯誤類結果 4 秒後自動恢復；「立即繼續掃描」可提前恢復', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'not-found' })
    const { scanner } = createScanner()
    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    scanner.handleDetectedContent(QR_CONTENT)
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('result')

    scanner.dismissResult()

    expect(scanner.state.value).toBe('scanning')
  })
})

describe('result 顯示期間 video 元素卸載又重新掛載（實測發現的黑屏 bug）', () => {
  // RedemptionScannerPage.vue 的 v-if="scanResult" 會在 result 顯示時把 <video> 整個卸載，
  // 恢復 scanning 後 Vue 會掛載一個全新的 DOM 節點；沿用中的 activeStream 必須重新指定到
  // 這個新節點的 srcObject，否則畫面維持黑屏（見 useRedemptionScanner.ts videoElement 的 watch）。
  it('恢復 scanning 後重新掛載的新 video 元素要拿到原本的 stream', async () => {
    vi.mocked(performRedemption).mockResolvedValue({ kind: 'not-found' })
    const { scanner } = createScanner()
    const videoBeforeResult = {} as HTMLVideoElement
    scanner.videoElement.value = videoBeforeResult

    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    expect(videoBeforeResult.srcObject).toBe(FAKE_STREAM)

    scanner.handleDetectedContent(QR_CONTENT)
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('result')

    // 模擬 Vue 因 v-if="scanResult" 卸載 <video>
    scanner.videoElement.value = null
    await Promise.resolve()

    scanner.dismissResult()
    expect(scanner.state.value).toBe('scanning')

    // 模擬 Vue 恢復 scanning 後重新掛載出一個全新的 <video> DOM 節點
    const videoAfterResume = {} as HTMLVideoElement
    scanner.videoElement.value = videoAfterResume
    await Promise.resolve()

    expect(videoAfterResume.srcObject).toBe(FAKE_STREAM)
  })
})

describe('掃描模式常駐手動輸入切換（決策 6）', () => {
  // 對應 AC: ADMIN-REDEEM-MANUAL-SWITCH
  it('scanning 狀態下可切換到手動輸入，不需等待相機判定失敗', async () => {
    const { scanner } = createScanner()
    scanner.mount()
    await Promise.resolve()
    await Promise.resolve()
    expect(scanner.state.value).toBe('scanning')

    scanner.switchToManualInput()

    expect(scanner.manualInputActive.value).toBe(true)
    expect(scanner.state.value).toBe('scanning') // 相機串流不需要因此中斷
  })
})
