import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { ref } from 'vue'
import RedemptionScannerPage from './RedemptionScannerPage.vue'
import { useRedemptionScanner } from '../../composables/useRedemptionScanner'
import type { ScanResultKind, ScannerState } from '../../composables/useRedemptionScanner'

vi.mock('../../composables/useRedemptionScanner')

function createFakeScanner(initialState: ScannerState = 'scanning') {
  const state = ref<ScannerState>(initialState)
  const manualInputActive = ref(false)
  const scanResult = ref<ScanResultKind | null>(null)
  const fake = {
    state,
    manualInputActive,
    scanResult,
    videoElement: ref(null),
    mount: vi.fn(),
    unmount: vi.fn(),
    switchToManualInput: vi.fn(() => {
      manualInputActive.value = true
    }),
    cancelManualInput: vi.fn(() => {
      manualInputActive.value = false
    }),
    submitManualRedemption: vi.fn().mockResolvedValue({ formatValid: true }),
    retryCamera: vi.fn(),
    dismissResult: vi.fn(),
    handleHidden: vi.fn(),
    handleVisible: vi.fn(),
    handleDetectedContent: vi.fn(),
  }
  vi.mocked(useRedemptionScanner).mockReturnValue(fake)
  return fake
}

function mountPage() {
  return mount(RedemptionScannerPage, { global: { plugins: [ElementPlus] } })
}

describe('RedemptionScannerPage', () => {
  // 對應 AC: ADMIN-REDEEM-TRUST-LABEL
  it('掃描模式顯示「已驗證簽章」標示', () => {
    createFakeScanner('scanning')
    const wrapper = mountPage()

    expect(wrapper.text()).toContain('已驗證簽章')
    expect(wrapper.text()).not.toContain('Admin 信任操作，未驗證簽章')
  })

  // 對應 AC: ADMIN-REDEEM-TRUST-LABEL
  it('手動輸入模式顯示「Admin 信任操作，未驗證簽章」標示', async () => {
    const fake = createFakeScanner('scanning')
    const wrapper = mountPage()

    fake.manualInputActive.value = true
    await wrapper.vm.$nextTick()

    expect(wrapper.text()).toContain('Admin 信任操作，未驗證簽章')
    expect(wrapper.text()).not.toContain('已驗證簽章')
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-SWITCH
  it('scanning 狀態下點擊「改用手動輸入」按鈕，畫面切換為手動輸入表單', async () => {
    createFakeScanner('scanning')
    const wrapper = mountPage()
    expect(wrapper.find('input').exists()).toBe(false) // 掃描畫面沒有 Ticket ID 輸入框

    const button = wrapper.findAll('button').find((b) => b.text() === '改用手動輸入')
    expect(button).toBeDefined()
    await button!.trigger('click')

    expect(wrapper.find('input').exists()).toBe(true)
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED
  it('unsupported 狀態下以手動輸入表單為主體，不顯示「重新嘗試相機」按鈕', () => {
    createFakeScanner('unsupported')
    const wrapper = mountPage()

    expect(wrapper.find('input').exists()).toBe(true)
    expect(wrapper.findAll('button').find((b) => b.text() === '重新嘗試相機')).toBeUndefined()
    expect(wrapper.text()).toContain('此瀏覽器不支援相機掃描')
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE
  it.each([
    ['permission-denied', '相機權限被拒絕'],
    ['camera-unavailable', '找不到可用相機'],
    ['error', '相機初始化發生錯誤'],
  ] as const)('%s 狀態下以手動輸入表單為主體，顯示「重新嘗試相機」按鈕與對應說明', (state, expectedText) => {
    createFakeScanner(state)
    const wrapper = mountPage()

    expect(wrapper.find('input').exists()).toBe(true)
    expect(wrapper.findAll('button').find((b) => b.text() === '重新嘗試相機')).toBeDefined()
    expect(wrapper.text()).toContain(expectedText)
  })
})
