import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import RedemptionScannerPage from './RedemptionScannerPage.vue'
import { useRedemptionScanner } from '../../composables/useRedemptionScanner'
import type { ScanResultKind, ScannerState } from '../../composables/useRedemptionScanner'
import * as adminApi from '../../api/admin'
import { useAuthStore } from '../../stores/auth'

vi.mock('../../composables/useRedemptionScanner')
vi.mock('../../api/admin')

const SCANNED_TRUST_LABEL = '已驗證簽章'
const MANUAL_TRUST_LABEL = '操作人員信任操作，未驗證簽章'

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
  it('[ADMIN-REDEEM-TRUST-LABEL] 掃描模式顯示「已驗證簽章」標示，不顯示手動模式標示', () => {
    createFakeScanner('scanning')
    const wrapper = mountPage()

    expect(wrapper.text()).toContain(SCANNED_TRUST_LABEL)
    expect(wrapper.text()).not.toContain(MANUAL_TRUST_LABEL)
  })

  // 手動輸入的信任對象已從 Admin 擴大為已切換 Organizer 的操作人員（design.md Decision 4），
  // 標示不得再暗示只有平台 Admin 可操作。
  it('[ADMIN-REDEEM-TRUST-LABEL] 由掃描模式切換到手動輸入後顯示「操作人員信任操作，未驗證簽章」標示', async () => {
    createFakeScanner('scanning')
    const wrapper = mountPage()

    const button = wrapper.findAll('button').find((b) => b.text() === '改用手動輸入')
    await button!.trigger('click')

    const manualLabel = wrapper.find('.trust-label').text()
    expect(manualLabel).toBe(MANUAL_TRUST_LABEL)
    expect(manualLabel).not.toContain('Admin')
    expect(wrapper.text()).not.toContain(SCANNED_TRUST_LABEL)
    // 兩種模式語意必須可區分：只有掃描模式是「已驗證」，只有手動模式是「未驗證」。
    expect(manualLabel).not.toBe(SCANNED_TRUST_LABEL)
    expect(SCANNED_TRUST_LABEL).toContain('已驗證')
    expect(SCANNED_TRUST_LABEL).not.toContain('未驗證')
    expect(manualLabel).toContain('未驗證')
  })

  it('[ADMIN-REDEEM-MANUAL-SWITCH] scanning 狀態下點擊「改用手動輸入」按鈕，畫面切換為手動輸入表單', async () => {
    createFakeScanner('scanning')
    const wrapper = mountPage()
    expect(wrapper.find('input').exists()).toBe(false) // 掃描畫面沒有 Ticket ID 輸入框

    const button = wrapper.findAll('button').find((b) => b.text() === '改用手動輸入')
    expect(button).toBeDefined()
    await button!.trigger('click')

    expect(wrapper.find('input').exists()).toBe(true)
  })

  it('[ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED] unsupported 狀態下以手動輸入表單為主體，不顯示「重新嘗試相機」按鈕', () => {
    createFakeScanner('unsupported')
    const wrapper = mountPage()

    expect(wrapper.find('input').exists()).toBe(true)
    expect(wrapper.findAll('button').find((b) => b.text() === '重新嘗試相機')).toBeUndefined()
    expect(wrapper.text()).toContain('此瀏覽器不支援相機掃描')
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE（8.7.16a～c 的畫面面向）
  it.each([
    ['permission-denied', '相機權限被拒絕'],
    ['camera-unavailable', '找不到可用相機'],
    ['error', '相機初始化發生錯誤'],
  ] as const)('[ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE] %s 狀態下以手動輸入表單為主體，顯示「重新嘗試相機」按鈕與對應說明', (state, expectedText) => {
    createFakeScanner(state)
    const wrapper = mountPage()

    expect(wrapper.find('input').exists()).toBe(true)
    expect(wrapper.findAll('button').find((b) => b.text() === '重新嘗試相機')).toBeDefined()
    expect(wrapper.text()).toContain(expectedText)
  })

  // 對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE（8.7.16d）：Requirement 要求三種原因不得共用同一句籠統訊息，
  // 逐列 toContain 抓不到三列被改成同一句的退化。
  it('[ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE] 三種可重試的相機失敗原因顯示彼此不同的說明文字', () => {
    const reasonTexts = (['permission-denied', 'camera-unavailable', 'error'] as const).map((state) => {
      createFakeScanner(state)
      return mountPage().find('.camera-status').text()
    })

    expect(new Set(reasonTexts).size).toBe(3)
  })

  // 8.7a：防止日後在核銷頁內部重新加入依 isAdmin 的隱藏或阻擋——這種退化路由守衛測試抓不到。
  // 使用真實 composable（只把相機能力設為不可用，讓手動輸入成為主體），mock 最底層的 redeemTicket。
  it('非 Admin 但已切換 Organizer 的操作人員可送出手動核銷', async () => {
    setActivePinia(createPinia())
    const authStore = useAuthStore()
    authStore.accessToken = `header.${btoa(JSON.stringify({ sub: '2', OrganizerId: 'org-1' }))}.signature`
    authStore.member = { id: '2', email: 'member@example.com', displayName: 'Member', role: 'Member', isActive: true }
    const actual = await vi.importActual<typeof import('../../composables/useRedemptionScanner')>('../../composables/useRedemptionScanner')
    vi.mocked(useRedemptionScanner).mockImplementation(() => actual.useRedemptionScanner({ isCameraCapable: () => false }))
    vi.mocked(adminApi.redeemTicket).mockReset()
    vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)
    const ticketId = '3f2504e0-4f89-41d3-9a0c-0305e82c3301'

    const wrapper = mount(RedemptionScannerPage, { global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.find('input').setValue(ticketId)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.redeemTicket).toHaveBeenCalledWith(ticketId, null)
    expect(wrapper.text()).toContain('核銷成功')
  })
})
