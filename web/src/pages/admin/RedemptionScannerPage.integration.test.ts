import { describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import RedemptionScannerPage from './RedemptionScannerPage.vue'
import * as cameraScanner from '../../utils/cameraScanner'

// 不 mock useRedemptionScanner 本身：RedemptionScannerPage.test.ts 完整 mock 了 composable，
// 純粹測樣板渲染邏輯，沒有機會驗證 <video> 元素是否真的綁定到 composable 回傳的
// videoElement ref——這個綁定斷開時（例如樣板寫成 ref="videoElement" 卻沒在
// <script setup> 解構出同名變數），所有既有測試仍會全數通過，因為它們都繞過了真實的
// template ref 機制。這裡只 mock 最底層的相機/解碼技術細節（cameraScanner.ts），
// 讓真正的 composable 與樣板走一次完整流程，直接斷言 DOM <video> 元素的
// srcObject 確實被設成相機 stream，藉此涵蓋 template ref 綁定本身。
vi.mock('../../utils/cameraScanner', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../utils/cameraScanner')>()
  return { ...actual, isCameraCapable: vi.fn(), openCameraStream: vi.fn() }
})

describe('RedemptionScannerPage（真實 composable，僅 mock 相機底層 API）', () => {
  it('相機初始化成功後，video 元素的 srcObject 確實被設成取得的 stream（驗證 template ref 綁定未斷開）', async () => {
    const fakeStream = { getTracks: () => [] } as unknown as MediaStream
    vi.mocked(cameraScanner.isCameraCapable).mockReturnValue(true)
    vi.mocked(cameraScanner.openCameraStream).mockResolvedValue(fakeStream)

    const wrapper = mount(RedemptionScannerPage, { global: { plugins: [ElementPlus] } })
    await flushPromises()
    await flushPromises()

    const video = wrapper.find('video').element as HTMLVideoElement
    expect(video.srcObject).toBe(fakeStream)

    wrapper.unmount()
  })
})
