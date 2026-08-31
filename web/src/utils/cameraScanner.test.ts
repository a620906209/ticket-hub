import { describe, expect, it } from 'vitest'
import { classifyCameraError, shouldDecodeNow } from './cameraScanner'

describe('shouldDecodeNow', () => {
  // 對應 design.md 決策 1（解碼節流）
  it('間隔小於節流門檻時回傳 false（略過解碼）', () => {
    expect(shouldDecodeNow(1000, 1050, 80)).toBe(false)
  })

  it('間隔達到節流門檻時回傳 true（執行一次解碼）', () => {
    expect(shouldDecodeNow(1000, 1080, 80)).toBe(true)
  })

  it('間隔剛好等於節流門檻時回傳 true', () => {
    expect(shouldDecodeNow(1000, 1080, 80)).toBe(true)
  })
})

describe('classifyCameraError', () => {
  it('NotAllowedError 分類為 permission-denied', () => {
    expect(classifyCameraError(new DOMException('denied', 'NotAllowedError'))).toBe('permission-denied')
  })

  it('NotFoundError 分類為 camera-unavailable', () => {
    expect(classifyCameraError(new DOMException('no camera', 'NotFoundError'))).toBe('camera-unavailable')
  })

  it('OverconstrainedError（後鏡頭 constraint 不滿足）分類為 camera-unavailable', () => {
    expect(classifyCameraError(new DOMException('constraint', 'OverconstrainedError'))).toBe('camera-unavailable')
  })

  it('其他例外分類為 error', () => {
    expect(classifyCameraError(new DOMException('unknown', 'AbortError'))).toBe('error')
    expect(classifyCameraError(new Error('unexpected'))).toBe('error')
  })
})
