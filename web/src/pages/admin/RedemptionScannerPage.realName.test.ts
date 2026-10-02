import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import * as adminApi from '../../api/admin'
import { ApiError } from '../../api/httpClient'
import type { TicketHolder } from '../../types/apiResponses'
import RedemptionScannerPage from './RedemptionScannerPage.vue'

// 與 RedemptionScannerPage.test.ts 不同：這裡用「真的」composable 與 ticketRedemptionOutcome，
// 只把相機換成假的、把 api/admin mock 掉，才能以 API 呼叫次數驗證「重試只重查持票人」
// 「放棄不呼叫任何端點」這類跨層行為（admin-web-ui spec AWU-REDEEM-RN-*）。
const captured = vi.hoisted(() => ({
  scanner: null as null | { handleDetectedContent: (content: string) => void },
  pendingFrame: null as null | ((timestamp: number) => void),
}))

vi.mock('../../api/admin', () => ({
  redeemTicket: vi.fn(),
  getTicketHolder: vi.fn(),
}))

vi.mock('../../composables/useRedemptionScanner', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../composables/useRedemptionScanner')>()
  return {
    ...actual,
    useRedemptionScanner: () => {
      const scanner = actual.useRedemptionScanner({
        isCameraCapable: () => true,
        openCameraStream: () => Promise.resolve({} as MediaStream),
        stopCameraStream: () => {},
        classifyCameraError: () => 'error',
        requestFrame: (callback) => {
          captured.pendingFrame = callback
          return 1
        },
        cancelFrame: () => {
          captured.pendingFrame = null
        },
        isFrameReady: () => true,
        readFrame: () => ({}) as ImageData,
        decodeQrFromImageData: () => OTHER_QR_CONTENT,
        shouldDecodeNow: () => true,
      })
      captured.scanner = scanner
      return scanner
    },
  }
})

const TICKET_ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6'
const QR_CONTENT = `${TICKET_ID}.the-signature`
const OTHER_QR_CONTENT = '7c9e6679-7425-40de-944b-e07fc1f90ae7.other-signature'

const holder: TicketHolder = {
  ticketId: TICKET_ID,
  ticketStatus: 'Issued',
  isRealNameRequired: true,
  holderRealName: '王小明',
  holderNationalIdLast4: '1234',
}

function holderVerificationRequired(): ApiError {
  return new ApiError(409, { status: 409, title: 'HolderVerificationRequired' })
}

async function mountScanningPage(): Promise<VueWrapper> {
  const wrapper = mount(RedemptionScannerPage, { global: { plugins: [ElementPlus] } })
  await flushPromises()
  return wrapper
}

async function scanTicket(content = QR_CONTENT): Promise<void> {
  captured.scanner!.handleDetectedContent(content)
  await flushPromises()
}

function findButton(wrapper: VueWrapper, text: string) {
  return wrapper.findAll('button').find((b) => b.text() === text)
}

async function clickButton(wrapper: VueWrapper, text: string): Promise<void> {
  const button = findButton(wrapper, text)
  if (!button) throw new Error(`找不到「${text}」按鈕`)
  await button.trigger('click')
  await flushPromises()
}

function holderPanel(wrapper: VueWrapper) {
  return wrapper.find('section.holder-panel')
}

// 結果橫幅與面板互斥；面板顯示時不得出現任何失敗類訊息。
function expectNoFailureMessage(wrapper: VueWrapper): void {
  expect(wrapper.find('.result-banner').exists()).toBe(false)
  expect(wrapper.text()).not.toContain('系統發生錯誤')
  expect(wrapper.text()).not.toContain('此票券已核銷過')
}

describe('RedemptionScannerPage 持票人確認（real-name-verification）', () => {
  beforeEach(() => {
    captured.scanner = null
    captured.pendingFrame = null
    vi.mocked(adminApi.redeemTicket).mockReset()
    vi.mocked(adminApi.getTicketHolder).mockReset()
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('[AWU-REDEEM-RN-001] 核銷回 HolderVerificationRequired 時查詢持票人並顯示姓名、末四碼與兩個按鈕，不顯示錯誤', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()

    await scanTicket()

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.redeemTicket).toHaveBeenCalledWith(TICKET_ID, 'the-signature')
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledWith(TICKET_ID)
    expect(wrapper.find('[data-testid="holder-real-name"]').text()).toBe('王小明')
    expect(wrapper.find('[data-testid="holder-national-id-last4"]').text()).toBe('1234')
    expect(holderPanel(wrapper).text()).toContain('持票人即訂購會員')
    expect(findButton(wrapper, '確認核銷')).toBeDefined()
    expect(findButton(wrapper, '放棄')).toBeDefined()
    expectNoFailureMessage(wrapper)
    expect(wrapper.find('video').exists()).toBe(false)
  })

  it('[AWU-REDEEM-RN-002] 確認核銷以同一 Ticket ID 與原簽章帶 isHolderVerified = true 重送，成功後顯示核銷成功', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired()).mockResolvedValueOnce(undefined)
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await clickButton(wrapper, '確認核銷')

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(2)
    expect(adminApi.redeemTicket).toHaveBeenNthCalledWith(2, TICKET_ID, 'the-signature', true)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('.result-banner').text()).toContain('核銷成功')
  })

  it('[AWU-REDEEM-RN-003] 放棄後面板關閉、回到掃描狀態，未再呼叫核銷端點', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await clickButton(wrapper, '放棄')

    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('video').exists()).toBe(true)
    expect(wrapper.find('.result-banner').exists()).toBe(false)
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })

  it('[AWU-REDEEM-RN-016] 掃碼路徑放棄後相機仍讀到同一張票不呼叫任何端點；讀到另一張票才正常核銷', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired()).mockResolvedValueOnce(undefined)
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()
    await clickButton(wrapper, '放棄')

    await scanTicket()
    await scanTicket()

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('video').exists()).toBe(true)

    await scanTicket(OTHER_QR_CONTENT)

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(2)
    expect(adminApi.redeemTicket).toHaveBeenLastCalledWith('7c9e6679-7425-40de-944b-e07fc1f90ae7', 'other-signature')
    expect(wrapper.find('.result-banner').text()).toContain('核銷成功')
  })

  it('[AWU-REDEEM-RN-004] 面板顯示超過結果橫幅的自動恢復時間仍不消失，也未呼叫核銷端點', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await vi.advanceTimersByTimeAsync(60_000)
    await flushPromises()

    expect(holderPanel(wrapper).exists()).toBe(true)
    expect(wrapper.find('[data-testid="holder-real-name"]').text()).toBe('王小明')
    expect(wrapper.find('video').exists()).toBe(false)
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })

  it('[AWU-REDEEM-RN-005] 面板顯示期間相機偵測到同一張或另一張票都不呼叫任何端點', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await scanTicket(QR_CONTENT)
    await scanTicket(OTHER_QR_CONTENT)
    // 解碼迴圈也必須已停止：若還有排定的影格，觸發它（假解碼器回傳另一張票）也不得呼叫端點。
    captured.pendingFrame?.(1_000)
    await flushPromises()

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="holder-real-name"]').text()).toBe('王小明')
  })

  it('[AWU-REDEEM-RN-006] 確認核銷處理中再次點擊只送出一次核銷請求', async () => {
    let resolveConfirm!: () => void
    vi.mocked(adminApi.redeemTicket)
      .mockRejectedValueOnce(holderVerificationRequired())
      .mockImplementationOnce(() => new Promise<void>((resolve) => (resolveConfirm = resolve)))
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await findButton(wrapper, '確認核銷')!.trigger('click')
    await flushPromises()
    expect(findButton(wrapper, '確認核銷')!.attributes('disabled')).toBeDefined()
    expect(findButton(wrapper, '放棄')!.attributes('disabled')).toBeDefined()
    await findButton(wrapper, '確認核銷')!.trigger('click')
    await flushPromises()

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(2)
    resolveConfirm()
    await flushPromises()
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(2)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.result-banner').text()).toContain('核銷成功')
  })

  it.each([
    ['AWU-REDEEM-RN-007', '網路錯誤', () => new TypeError('Failed to fetch')],
    ['AWU-REDEEM-RN-011', '500', () => new ApiError(500, null)],
    ['AWU-REDEEM-RN-012', '404', () => new ApiError(404, { status: 404 })],
    ['AWU-REDEEM-RN-012', '403', () => new ApiError(403, { status: 403 })],
  ])('[%s] 查詢持票人遇到%s時顯示可重試錯誤與「重試」「放棄」，不顯示確認面板內容、未再呼叫核銷端點', async (_id, _label, createError) => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockRejectedValue(createError())
    const wrapper = await mountScanningPage()

    await scanTicket()

    expect(holderPanel(wrapper).text()).toContain('查詢持票人資料失敗')
    expect(findButton(wrapper, '重試')).toBeDefined()
    expect(findButton(wrapper, '放棄')).toBeDefined()
    expect(findButton(wrapper, '確認核銷')).toBeUndefined()
    expect(wrapper.find('[data-testid="holder-real-name"]').exists()).toBe(false)
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })

  it('[AWU-REDEEM-RN-013] 重試只重新查詢持票人：成功後顯示面板，核銷 1 次、查詢 2 次', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockRejectedValueOnce(new TypeError('Failed to fetch')).mockResolvedValueOnce(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await clickButton(wrapper, '重試')

    expect(wrapper.find('[data-testid="holder-real-name"]').text()).toBe('王小明')
    expect(findButton(wrapper, '確認核銷')).toBeDefined()
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(2)
  })

  it('[AWU-REDEEM-RN-014] 查詢失敗後放棄回到掃描狀態，核銷只呼叫 1 次且未帶 isHolderVerified', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockRejectedValue(new ApiError(500, null))
    const wrapper = await mountScanningPage()
    await scanTicket()

    await clickButton(wrapper, '放棄')

    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('video').exists()).toBe(true)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.redeemTicket).toHaveBeenCalledWith(TICKET_ID, 'the-signature')
  })

  it('[AWU-REDEEM-RN-015] 查詢持票人回 409 時顯示既有「已核銷過」結果，不顯示面板、未再呼叫核銷端點', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))
    const wrapper = await mountScanningPage()

    await scanTicket()

    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('.result-banner').text()).toContain('此票券已核銷過')
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })

  it('[AWU-REDEEM-RN-008] 手動輸入路徑同樣顯示面板，確認時帶 isHolderVerified = true 且不帶簽章', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired()).mockResolvedValueOnce(undefined)
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await clickButton(wrapper, '改用手動輸入')
    await wrapper.find('input').setValue(TICKET_ID)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[data-testid="holder-real-name"]').text()).toBe('王小明')
    expect(wrapper.find('form').exists()).toBe(false)
    await clickButton(wrapper, '確認核銷')

    expect(adminApi.redeemTicket).toHaveBeenNthCalledWith(1, TICKET_ID, null)
    expect(adminApi.redeemTicket).toHaveBeenNthCalledWith(2, TICKET_ID, null, true)
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(2)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.result-banner').text()).toContain('核銷成功')
  })

  it('[AWU-REDEEM-RN-017] 相機可用時切到手動輸入送出後放棄，回到手動輸入表單而非相機', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await clickButton(wrapper, '改用手動輸入')
    await wrapper.find('input').setValue(TICKET_ID)
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(holderPanel(wrapper).exists()).toBe(true)

    await clickButton(wrapper, '放棄')

    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('form').exists()).toBe(true)
    expect(wrapper.find('video').exists()).toBe(false)
    expect(findButton(wrapper, '改用相機掃描')).toBeDefined()
    // 表單顯示期間相機解碼迴圈不得送出核銷（假相機每一幀都解出另一張票）。
    captured.pendingFrame?.(1000)
    await flushPromises()
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })

  it('[AWU-REDEEM-RN-009] 姓名含 HTML 標籤時以純文字顯示，DOM 不產生 img 元素', async () => {
    const maliciousName = '<img src=x onerror=alert(1)>'
    vi.mocked(adminApi.redeemTicket).mockRejectedValueOnce(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue({ ...holder, holderRealName: maliciousName })
    const wrapper = await mountScanningPage()

    await scanTicket()

    expect(wrapper.find('[data-testid="holder-real-name"]').text()).toBe(maliciousName)
    expect(holderPanel(wrapper).find('img').exists()).toBe(false)
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })

  it('[AWU-REDEEM-RN-010] 不需實名活動核銷成功時直接顯示成功結果，未查詢持票人', async () => {
    vi.mocked(adminApi.redeemTicket).mockResolvedValue(undefined)
    const wrapper = await mountScanningPage()

    await scanTicket()

    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(1)
    expect(adminApi.redeemTicket).toHaveBeenCalledWith(TICKET_ID, 'the-signature')
    expect(adminApi.getTicketHolder).not.toHaveBeenCalled()
    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('.result-banner').text()).toContain('核銷成功')
  })

  it('確認核銷時仍回 HolderVerificationRequired 屬後端異常：顯示系統錯誤，不再開一次面板', async () => {
    vi.mocked(adminApi.redeemTicket).mockRejectedValue(holderVerificationRequired())
    vi.mocked(adminApi.getTicketHolder).mockResolvedValue(holder)
    const wrapper = await mountScanningPage()
    await scanTicket()

    await clickButton(wrapper, '確認核銷')

    expect(holderPanel(wrapper).exists()).toBe(false)
    expect(wrapper.find('.result-banner').text()).toContain('系統發生錯誤')
    expect(adminApi.redeemTicket).toHaveBeenCalledTimes(2)
    expect(adminApi.getTicketHolder).toHaveBeenCalledTimes(1)
  })
})
