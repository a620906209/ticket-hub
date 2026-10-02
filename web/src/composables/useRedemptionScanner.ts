import { ref, watch, type Ref } from 'vue'
import {
  classifyCameraError as defaultClassifyCameraError,
  decodeQrFromImageData as defaultDecodeQrFromImageData,
  isCameraCapable as defaultIsCameraCapable,
  isVideoFrameReady as defaultIsVideoFrameReady,
  openCameraStream as defaultOpenCameraStream,
  shouldDecodeNow as defaultShouldDecodeNow,
  stopCameraStream as defaultStopCameraStream,
  type CameraErrorKind,
} from '../utils/cameraScanner'
import { lookupTicketHolder, performRedemption, type RedemptionOutcome } from '../utils/ticketRedemptionOutcome'
import type { TicketHolder } from '../types/apiResponses'
import { parseTicketIdFromManualInput, parseTicketIdFromQrContent } from '../utils/ticketRedemptionParsing'

// 相機生命週期與掃描狀態機（design.md 決策 4）。相機/解碼相關的技術細節全部透過 deps 注入，
// 預設值指向真實瀏覽器 API／utils/cameraScanner.ts；單元測試注入假實作，不依賴 jsdom 沒有的
// getUserMedia／canvas 像素資料／真實 requestAnimationFrame 時間軸（見 design.md Risks）。

export type ScannerState =
  | 'initializing'
  | 'scanning'
  | 'processing'
  | 'result'
  | 'holder-verification'
  | 'camera-unavailable'
  | 'permission-denied'
  | 'unsupported'
  | 'error'

// holder-verification-required 不是結果橫幅，而是進入持票人確認面板（real-name-verification design.md 決策 8）。
export type ScanResultKind = Exclude<RedemptionOutcome['kind'], 'holder-verification-required'> | 'unrecognized'

// 持票人確認面板的階段：loading 查詢中、loaded 顯示資料待確認、lookup-failed 查詢失敗待重試/放棄、
// confirming 已送出確認核銷（按鈕停用，防止重複送出）。
export type HolderVerificationPhase = 'loading' | 'loaded' | 'lookup-failed' | 'confirming'

export interface HolderVerification {
  ticketId: string
  signature: string | null
  phase: HolderVerificationPhase
  holder: TicketHolder | null
  /** 手動輸入路徑沒有簽章（既有手動核銷慣例），放棄時要回到手動輸入表單而非相機。 */
  isManualEntry: boolean
}

const DECODE_INTERVAL_MS = 80 // 約 12 次/秒，落在決策 1 的 10–15 次/秒範圍內

export interface RedemptionScannerDeps {
  isCameraCapable: () => boolean
  openCameraStream: () => Promise<MediaStream>
  stopCameraStream: (stream: MediaStream) => void
  classifyCameraError: (error: unknown) => CameraErrorKind
  requestFrame: (callback: (timestamp: number) => void) => number
  cancelFrame: (id: number) => void
  isFrameReady: (video: HTMLVideoElement) => boolean
  readFrame: (video: HTMLVideoElement) => ImageData | null
  decodeQrFromImageData: (imageData: ImageData) => string | null
  shouldDecodeNow: (lastDecodedAtMs: number, nowMs: number, minIntervalMs: number) => boolean
}

// canvas／context 只在同一個 composable 實例內建立一次並重複使用（design.md 決策 1：
// 「canvas 與其 ImageData buffer 只在初始化時建立一次並重複使用，不逐幀重新配置」），
// 只在影格尺寸真的改變時才調整 canvas 大小，不是每次解碼都重新配置。
function createDefaultReadFrame(): (video: HTMLVideoElement) => ImageData | null {
  let canvas: HTMLCanvasElement | null = null
  let context: CanvasRenderingContext2D | null = null

  return (video: HTMLVideoElement): ImageData | null => {
    if (!canvas) {
      canvas = document.createElement('canvas')
      context = canvas.getContext('2d')
    }
    if (!context) {
      return null
    }
    if (canvas.width !== video.videoWidth || canvas.height !== video.videoHeight) {
      canvas.width = video.videoWidth
      canvas.height = video.videoHeight
    }
    context.drawImage(video, 0, 0, canvas.width, canvas.height)
    return context.getImageData(0, 0, canvas.width, canvas.height)
  }
}

function createDefaultDeps(): RedemptionScannerDeps {
  return {
    isCameraCapable: defaultIsCameraCapable,
    openCameraStream: defaultOpenCameraStream,
    stopCameraStream: defaultStopCameraStream,
    classifyCameraError: defaultClassifyCameraError,
    requestFrame: (callback) => window.requestAnimationFrame(callback),
    cancelFrame: (id) => window.cancelAnimationFrame(id),
    isFrameReady: defaultIsVideoFrameReady,
    readFrame: createDefaultReadFrame(),
    decodeQrFromImageData: defaultDecodeQrFromImageData,
    shouldDecodeNow: defaultShouldDecodeNow,
  }
}

export function useRedemptionScanner(overrides: Partial<RedemptionScannerDeps> = {}) {
  // readFrame 的 canvas/context 快取是每個 composable 實例（也就是每次相機掃描頁面掛載）
  // 各自獨立一份，故 defaults 必須每次呼叫 useRedemptionScanner() 時重新建立，
  // 不能是模組層級的共用單例（否則多個實例會共用同一份 canvas 狀態）。
  const deps: RedemptionScannerDeps = { ...createDefaultDeps(), ...overrides }

  const state = ref<ScannerState>('initializing')
  const manualInputActive = ref(false)
  const scanResult: Ref<ScanResultKind | null> = ref(null)
  const holderVerification: Ref<HolderVerification | null> = ref(null)
  const videoElement: Ref<HTMLVideoElement | null> = ref(null)

  // result 顯示期間 <video> 會因為 RedemptionScannerPage.vue 的 v-if="scanResult" 被 Vue 卸載，
  // 結果消失後恢復 scanning 會重新掛載一個全新的 <video> DOM 節點。resumeScanningFromResult()
  // 沿用既有 activeStream 時不會重新呼叫 attemptInitializeCamera()，若不在這裡補上 srcObject，
  // 新節點永遠拿不到影像來源，畫面會維持黑屏直到整頁刷新（實測發現）。
  watch(videoElement, (element) => {
    if (element && activeStream) {
      element.srcObject = activeStream
    }
  })

  let generation = 0
  let activeStream: MediaStream | null = null
  let frameId: number | null = null
  let lastDecodedAtMs = 0
  let lastSubmittedContent: string | null = null
  let stateBeforeHidden: ScannerState | null = null
  let resultTimer: ReturnType<typeof setTimeout> | null = null
  let pendingResultKind: ScanResultKind | null = null
  let mounted = false

  function stopDecodeLoop(): void {
    if (frameId !== null) {
      deps.cancelFrame(frameId)
      frameId = null
    }
  }

  function releaseCameraResources(): void {
    stopDecodeLoop()
    if (activeStream) {
      deps.stopCameraStream(activeStream)
      activeStream = null
    }
    if (videoElement.value) {
      videoElement.value.srcObject = null
    }
  }

  function clearResultTimer(): void {
    if (resultTimer !== null) {
      clearTimeout(resultTimer)
      resultTimer = null
    }
  }

  function decodeTick(timestamp: number): void {
    frameId = deps.requestFrame(decodeTick)

    if (state.value !== 'scanning' || manualInputActive.value) {
      return
    }
    const video = videoElement.value
    if (!video || !deps.isFrameReady(video)) {
      return
    }
    if (!deps.shouldDecodeNow(lastDecodedAtMs, timestamp, DECODE_INTERVAL_MS)) {
      return
    }
    lastDecodedAtMs = timestamp

    const frame = deps.readFrame(video)
    if (!frame) {
      return
    }
    const content = deps.decodeQrFromImageData(frame)
    if (content) {
      handleDetectedContent(content)
    }
  }

  function startDecodeLoop(): void {
    lastDecodedAtMs = 0
    frameId = deps.requestFrame(decodeTick)
  }

  async function attemptInitializeCamera(): Promise<void> {
    generation += 1
    const myGeneration = generation
    state.value = 'initializing'
    manualInputActive.value = false

    if (!deps.isCameraCapable()) {
      state.value = 'unsupported'
      return
    }

    try {
      const stream = await deps.openCameraStream()
      if (myGeneration !== generation) {
        // 這次初始化已過期（切背景或 unmount）：立即釋放，不掛載也不啟動迴圈（決策 4 race condition 保護）
        deps.stopCameraStream(stream)
        return
      }
      activeStream = stream
      if (videoElement.value) {
        videoElement.value.srcObject = stream
      }
      state.value = 'scanning'
      startDecodeLoop()
    } catch (error) {
      if (myGeneration !== generation) {
        return
      }
      state.value = deps.classifyCameraError(error)
    }
  }

  // 結果顯示結束後要回到的狀態：掃描路徑一律回 'scanning'（重新取得相機）；手動輸入路徑
  // 依送出當下的狀態決定——相機正常時（'scanning'）結束後回相機掃描，相機本就不可用的四個
  // 備援狀態則結束後維持該狀態，不得因為手動核銷完成就誤觸自動重試相機（決策 4）。
  let resumeTargetState: ScannerState = 'scanning'

  function handleDetectedContent(content: string): void {
    // decodeTick 在面板期間本來就不解碼；這裡再擋一次，確保任何偵測來源都不會在比對證件時觸發 API。
    if (state.value === 'holder-verification') {
      return
    }
    const parsed = parseTicketIdFromQrContent(content)
    if (!parsed.recognized) {
      resumeTargetState = 'scanning'
      stopDecodeLoop()
      showResult('unrecognized')
      return
    }
    if (content === lastSubmittedContent) {
      return // 決策 7：當輪 dedupe，避免殘影重複觸發
    }
    resumeTargetState = 'scanning'
    stopDecodeLoop()
    state.value = 'processing'
    lastSubmittedContent = content
    performRedemption(parsed.ticketId, parsed.signature)
      .then((outcome) => handleRedemptionOutcome(outcome, parsed.ticketId, parsed.signature))
      .catch(() => showResult('system-error'))
  }

  function handleRedemptionOutcome(outcome: RedemptionOutcome, ticketId: string, signature: string | null): void {
    if (!mounted) return
    if (outcome.kind === 'holder-verification-required') {
      enterHolderVerification(ticketId, signature)
      return
    }
    showResult(outcome.kind)
  }

  // 面板期間 state 維持 'holder-verification'：decodeTick 只在 'scanning' 時解碼，相機偵測自然被忽略；
  // 也不啟動結果橫幅的自動恢復計時，只有「確認核銷」或「放棄」能離開（admin-web-ui spec）。
  function enterHolderVerification(ticketId: string, signature: string | null): void {
    if (!mounted) return
    stopDecodeLoop()
    state.value = 'holder-verification'
    holderVerification.value = { ticketId, signature, phase: 'loading', holder: null, isManualEntry: signature === null }
    void loadHolder()
  }

  async function loadHolder(): Promise<void> {
    const current = holderVerification.value
    if (!current) return
    current.phase = 'loading'
    const lookup = await lookupTicketHolder(current.ticketId)
    // 查詢期間已放棄（或 unmount）時丟棄結果，不得重新開啟面板。
    if (holderVerification.value !== current) return

    if (lookup.kind === 'found') {
      current.holder = lookup.holder
      current.phase = 'loaded'
    } else if (lookup.kind === 'already-redeemed') {
      holderVerification.value = null
      showResult('already-redeemed')
    } else {
      current.phase = 'lookup-failed'
    }
  }

  /** 「重試」：只重新查詢持票人，不重送核銷請求。 */
  function retryHolderLookup(): void {
    if (holderVerification.value?.phase !== 'lookup-failed') return
    void loadHolder()
  }

  /** 「確認核銷」：以原 Ticket ID 與原簽章（手動輸入為 null）重送，帶 isHolderVerified = true。 */
  async function confirmHolderVerification(): Promise<void> {
    const current = holderVerification.value
    if (!current || current.phase !== 'loaded') return
    current.phase = 'confirming'
    let kind: ScanResultKind
    try {
      const outcome = await performRedemption(current.ticketId, current.signature, true)
      // 已帶確認旗標仍要求確認屬於後端異常，不得再開一次面板形成迴圈。
      kind = outcome.kind === 'holder-verification-required' ? 'system-error' : outcome.kind
    } catch {
      kind = 'system-error'
    }
    if (holderVerification.value !== current) return
    holderVerification.value = null
    showResult(kind)
  }

  /** 「放棄」：不呼叫任何端點，回到進入面板前的輸入方式（掃碼回相機、手動輸入回表單）。 */
  function abandonHolderVerification(): void {
    const current = holderVerification.value
    if (!current || current.phase === 'confirming') return
    holderVerification.value = null
    const abandonedContent = lastSubmittedContent
    resumeScanningFromResult()
    if (current.isManualEntry) {
      manualInputActive.value = true
      return
    }
    // 放棄通常代表證件不符；鏡頭仍對著同一張票時不得立刻重開面板（AWU-REDEEM-RN-016），
    // 所以保留這張票的 dedupe 記憶，直到處理過另一張票後的恢復才清除。
    lastSubmittedContent = abandonedContent
  }

  function showResult(kind: ScanResultKind): void {
    if (!mounted) return
    if (document.hidden) {
      // 核銷請求在背景時完成：先保存結果，不進入 result 顯示、不啟動倒數計時，
      // 等切回前景（handleVisible）才真正顯示並開始倒數——否則計時器可能在使用者
      // 看不到畫面時就把結果清掉，甚至提前觸發重新初始化相機（決策 4）。
      pendingResultKind = kind
      return
    }
    applyResult(kind)
  }

  function applyResult(kind: ScanResultKind): void {
    scanResult.value = kind
    state.value = 'result'
    const durationMs = kind === 'success' ? 1500 : 4000
    clearResultTimer()
    resultTimer = setTimeout(() => resumeScanningFromResult(), durationMs)
  }

  function resumeScanningFromResult(): void {
    clearResultTimer()
    scanResult.value = null
    lastSubmittedContent = null // 決策 7：恢復 scanning 立即清除當輪 dedupe 記憶

    if (resumeTargetState !== 'scanning') {
      state.value = resumeTargetState
      return
    }
    if (activeStream) {
      state.value = 'scanning'
      startDecodeLoop()
    } else {
      void attemptInitializeCamera()
    }
  }

  /** 對應「立即繼續掃描」按鈕：錯誤類結果可提前恢復，不需等待自動計時。 */
  function dismissResult(): void {
    if (state.value !== 'result') {
      return
    }
    resumeScanningFromResult()
  }

  function switchToManualInput(): void {
    manualInputActive.value = true
  }

  function cancelManualInput(): void {
    manualInputActive.value = false
  }

  // 格式不合法時（決策 5）：不呼叫 API、不進入 result 顯示，由表單顯示行內錯誤訊息，
  // 不得讓格式錯誤流到後端變成 404 才顯示「查無此票」。
  async function submitManualRedemption(rawTicketId: string): Promise<{ formatValid: boolean }> {
    const parsed = parseTicketIdFromManualInput(rawTicketId)
    if (!parsed.valid) {
      return { formatValid: false }
    }

    // 相機仍在初始化（例如放棄手動輸入的面板後重新取得相機中）時送出：作廢這次初始化，
    // 否則它稍後完成會蓋掉 'processing'（甚至在請求途中開始解碼），結束後又回到 'initializing'
    // 而沒有任何初始化在進行，畫面卡在「初始化相機中…」。結果顯示完再重新取得相機。
    if (state.value === 'initializing') {
      generation += 1
      resumeTargetState = 'scanning'
    } else {
      resumeTargetState = state.value
    }
    stopDecodeLoop()
    manualInputActive.value = false
    state.value = 'processing'
    try {
      const outcome = await performRedemption(parsed.ticketId, null)
      handleRedemptionOutcome(outcome, parsed.ticketId, null)
    } catch {
      showResult('system-error')
    }
    return { formatValid: true }
  }

  function retryCamera(): void {
    void attemptInitializeCamera()
  }

  function handleHidden(): void {
    if (stateBeforeHidden !== null) {
      return // 已經處理過（避免重複觸發）
    }
    stateBeforeHidden = state.value
    if (state.value === 'result') {
      clearResultTimer()
    }
    if (state.value === 'initializing') {
      generation += 1 // 讓尚未 resolve 的 getUserMedia() 結果被視為過期
    }
    releaseCameraResources()
  }

  function handleVisible(): void {
    const previousState = stateBeforeHidden
    stateBeforeHidden = null
    if (previousState === null || !mounted) {
      return
    }

    switch (previousState) {
      case 'scanning':
      case 'initializing':
        void attemptInitializeCamera()
        return
      case 'processing':
        if (pendingResultKind !== null) {
          // 核銷請求已經在背景時完成並被 showResult() 暫存；現在才是使用者真正看得到
          // 畫面的時刻，在這裡才進入 result 顯示並開始倒數。
          const kind = pendingResultKind
          pendingResultKind = null
          applyResult(kind)
          return
        }
        // 呼叫本身未被取消、也還沒完成，會自然完成並呼叫 showResult()；不重新發送核銷請求。
        return
      case 'holder-verification':
        // 面板不自動消失；若確認核銷的結果在背景時完成並被暫存，現在才顯示。
        if (pendingResultKind !== null) {
          const kind = pendingResultKind
          pendingResultKind = null
          applyResult(kind)
        }
        return
      case 'result':
        // 保留已顯示的結果，重啟倒數；結果顯示完才由 resumeScanningFromResult() 重新初始化相機。
        clearResultTimer()
        resultTimer = setTimeout(() => resumeScanningFromResult(), scanResult.value === 'success' ? 1500 : 4000)
        return
      case 'unsupported':
      case 'permission-denied':
      case 'camera-unavailable':
      case 'error':
        // MUST NOT 自動重試，維持手動輸入表單（決策 4）。
        return
    }
  }

  function onVisibilityChange(): void {
    if (document.hidden) {
      handleHidden()
    } else {
      handleVisible()
    }
  }

  function mount(): void {
    mounted = true
    document.addEventListener('visibilitychange', onVisibilityChange)
    void attemptInitializeCamera()
  }

  function unmount(): void {
    mounted = false
    document.removeEventListener('visibilitychange', onVisibilityChange)
    generation += 1 // 任何尚未 resolve 的 getUserMedia() 結果視為過期，不得再更動狀態
    holderVerification.value = null
    clearResultTimer()
    releaseCameraResources()
  }

  return {
    state,
    manualInputActive,
    scanResult,
    holderVerification,
    videoElement,
    mount,
    unmount,
    switchToManualInput,
    cancelManualInput,
    submitManualRedemption,
    retryCamera,
    dismissResult,
    handleHidden,
    handleVisible,
    retryHolderLookup,
    confirmHolderVerification,
    abandonHolderVerification,
    // 暴露給測試直接呼叫：元件層的相機決策 4 生命週期／決策 7 dedupe 邏輯與「怎麼被偵測到
    // 一次内容」是兩件事，後者（decodeTick 的 rAF/節流時序）已由 cameraScanner.test.ts 的
    // shouldDecodeNow 獨立覆蓋，這裡讓測試能直接餵入「偵測到的內容」而不必模擬真實影格時序。
    handleDetectedContent,
  }
}

export type RedemptionScanner = ReturnType<typeof useRedemptionScanner>
