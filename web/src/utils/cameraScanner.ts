import jsQR from 'jsqr'

// 相機掃描的技術細節封裝成獨立、可在測試中 mock 的介面（design.md Risks：jsdom 沒有真實
// getUserMedia／相機／jsQR 影像輸入，元件的狀態機測試透過 vi.mock 這個模組來注入掃描結果）。

export type CameraErrorKind = 'permission-denied' | 'camera-unavailable' | 'error'

// getUserMedia 例外分類（design.md 決策 1、決策 4）：NotAllowedError 為使用者拒絕權限；
// NotFoundError／OverconstrainedError（含後鏡頭 constraint 不滿足）為無可用相機；其餘歸類為一般錯誤。
export function classifyCameraError(error: unknown): CameraErrorKind {
  if (error instanceof DOMException) {
    if (error.name === 'NotAllowedError') {
      return 'permission-denied'
    }
    if (error.name === 'NotFoundError' || error.name === 'OverconstrainedError') {
      return 'camera-unavailable'
    }
  }
  return 'error'
}

// 能力偵測（決策 1）：secure context 與 getUserMedia API 兩者皆存在才視為支援。
export function isCameraCapable(): boolean {
  return window.isSecureContext && typeof navigator.mediaDevices?.getUserMedia === 'function'
}

export function openCameraStream(): Promise<MediaStream> {
  return navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } })
}

export function stopCameraStream(stream: MediaStream): void {
  for (const track of stream.getTracks()) {
    track.stop()
  }
}

// 解碼頻率節流的純函式（決策 1，10–15 次/秒）：抽出來獨立測試，不依賴真實 rAF 時間軸。
export function shouldDecodeNow(lastDecodedAtMs: number, nowMs: number, minIntervalMs: number): boolean {
  return nowMs - lastDecodedAtMs >= minIntervalMs
}

// video 尚未有可用畫面資料時略過解碼，避免對空畫面呼叫 jsQR。
export function isVideoFrameReady(video: HTMLVideoElement): boolean {
  return video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA && video.videoWidth > 0
}

export function decodeQrFromImageData(imageData: ImageData): string | null {
  const result = jsQR(imageData.data, imageData.width, imageData.height)
  return result?.data ?? null
}
