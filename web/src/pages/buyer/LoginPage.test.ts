import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import LoginPage from './LoginPage.vue'
import { ApiError } from '../../api/httpClient'
import * as captchaApi from '../../api/captcha'

vi.mock('../../api/captcha')

const pushMock = vi.fn()
vi.mock('vue-router', () => ({
  useRoute: () => ({ query: {} }),
  useRouter: () => ({ push: pushMock }),
}))

const loginMock = vi.fn()
vi.mock('../../stores/auth', () => ({
  useAuthStore: () => ({
    login: loginMock,
    get isAdmin() {
      return false
    },
  }),
}))

function mountPage() {
  return mount(LoginPage, {
    global: { plugins: [ElementPlus], stubs: { RouterLink: true } },
  })
}

async function fillAndSubmit(wrapper: ReturnType<typeof mount>) {
  await wrapper.find('input[type="email"]').setValue('user@example.com')
  await wrapper.find('input[type="password"]').setValue('Password123')
  // 驗證碼輸入欄位：若前端要求必填，不先填寫會被前端驗證擋下，根本不會呼叫到 mock 的 loginMock。
  await wrapper.find('input[type="text"]').setValue('TEST')
  await flushPromises()
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('LoginPage', () => {
  beforeEach(() => {
    loginMock.mockReset()
    pushMock.mockReset()
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-1', imageBase64: 'base64-image-1' })
  })

  it('LRL-009：登入因請求頻率限制被拒絕（429）時，顯示友善提示，不顯示後端原始 title 字串', async () => {
    loginMock.mockRejectedValue(new ApiError(429, { status: 429, title: 'TooManyRequests' }))
    const wrapper = mountPage()

    await fillAndSubmit(wrapper)

    expect(wrapper.text()).toContain('登入嘗試過於頻繁，請稍後再試')
    expect(wrapper.text()).not.toContain('TooManyRequests')
  })

  it('登入失敗（非 429）沿用既有一般錯誤處理，不套用 429 的友善提示', async () => {
    loginMock.mockRejectedValue(new ApiError(401, { status: 401, title: 'Unauthorized' }))
    const wrapper = mountPage()

    await fillAndSubmit(wrapper)

    expect(wrapper.text()).not.toContain('登入嘗試過於頻繁，請稍後再試')
  })

  // 既有「登入成功導向買家端首頁」情境目前沒有自動化元件測試涵蓋（見 6.6）。
  it('提供正確帳密與驗證碼送出後呼叫登入 API 成功並導向買家端首頁', async () => {
    loginMock.mockResolvedValue(undefined)
    const wrapper = mountPage()

    await fillAndSubmit(wrapper)

    expect(loginMock).toHaveBeenCalledWith('user@example.com', 'Password123', 'captcha-token-1', 'TEST')
    expect(pushMock).toHaveBeenCalledWith('/')
  })

  // CAPTCHA-BW-004：登入頁載入時顯示驗證碼圖片與輸入欄位。
  it('CAPTCHA-BW-004：載入時顯示驗證碼圖片與輸入欄位', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('img[alt="驗證碼圖片"]').exists()).toBe(true)
    expect(wrapper.find('input[type="text"]').exists()).toBe(true)
  })

  // CAPTCHA-BW-002：驗證碼錯誤時顯示提示且自動換發。
  it('CAPTCHA-BW-002：驗證碼錯誤時顯示提示且自動換發新驗證碼', async () => {
    loginMock.mockRejectedValue(new ApiError(400, { status: 400, title: 'CaptchaInvalid', detail: '驗證碼錯誤或已逾時，請重新取得驗證碼。' }))
    const wrapper = mountPage()
    await flushPromises()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-2', imageBase64: 'base64-image-2' })

    await fillAndSubmit(wrapper)

    expect(wrapper.text()).toContain('驗證碼錯誤或已逾時，請重新取得驗證碼。')
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
  })

  // CAPTCHA-BW-003：手動刷新按鈕觸發換發。
  it('CAPTCHA-BW-003：點擊「看不清楚？換一張」觸發換發新驗證碼', async () => {
    const wrapper = mountPage()
    await flushPromises()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-2', imageBase64: 'base64-image-2' })

    await wrapper.findAll('button').find((b) => b.text().includes('換一張'))!.trigger('click')
    await flushPromises()

    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
  })

  // CAPTCHA-BW-005：初次載入 GET /api/captcha 失敗時顯示提示並停用送出按鈕。
  it('CAPTCHA-BW-005：初次載入驗證碼失敗時顯示提示、停用送出按鈕', async () => {
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockRejectedValue(new Error('network error'))
    const wrapper = mountPage()

    await flushPromises()

    expect(wrapper.text()).toContain('系統暫時無法取得驗證碼，請稍後再試')
    const submitButton = wrapper.find('button[type="submit"]')
    expect(submitButton.attributes('disabled')).toBeDefined()
  })

  // CAPTCHA-BW-006：刷新失敗時顯示提示、停用送出按鈕，不清空已填的 Email／密碼欄位。
  it('CAPTCHA-BW-006：刷新驗證碼失敗時顯示提示、停用送出按鈕，不清空已填欄位', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await wrapper.find('input[type="email"]').setValue('user@example.com')
    await wrapper.find('input[type="password"]').setValue('Password123')
    vi.mocked(captchaApi.getCaptcha).mockRejectedValue(new Error('network error'))

    await wrapper.findAll('button').find((b) => b.text().includes('換一張'))!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('系統暫時無法取得驗證碼，請稍後再試')
    const submitButton = wrapper.find('button[type="submit"]')
    expect(submitButton.attributes('disabled')).toBeDefined()
    expect((wrapper.find('input[type="email"]').element as HTMLInputElement).value).toBe('user@example.com')
    expect((wrapper.find('input[type="password"]').element as HTMLInputElement).value).toBe('Password123')
  })
})
