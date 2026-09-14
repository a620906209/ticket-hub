import { describe, expect, it, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import RegisterPage from './RegisterPage.vue'
import { ApiError } from '../../api/httpClient'
import * as authApi from '../../api/auth'
import * as captchaApi from '../../api/captcha'

vi.mock('../../api/captcha')
vi.mock('../../api/auth')

const pushMock = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: pushMock }),
}))

function mountPage() {
  return mount(RegisterPage, {
    global: { plugins: [ElementPlus], stubs: { RouterLink: true } },
  })
}

async function fillAndSubmit(wrapper: ReturnType<typeof mount>) {
  await wrapper.find('input[type="email"]').setValue('user@example.com')
  await wrapper.findAll('input[type="text"]')[0].setValue('Alice')
  await wrapper.find('input[type="password"]').setValue('Password123')
  await wrapper.findAll('input[type="text"]')[1].setValue('TEST')
  await flushPromises()
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('RegisterPage', () => {
  beforeEach(() => {
    vi.mocked(authApi.register).mockReset()
    pushMock.mockReset()
    vi.mocked(captchaApi.getCaptcha).mockReset()
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'captcha-token-1', imageBase64: 'base64-image-1' })
  })

  // CAPTCHA-BW-001：載入時顯示驗證碼圖片與輸入欄位。
  it('CAPTCHA-BW-001：載入時顯示驗證碼圖片與輸入欄位', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('img[alt="驗證碼圖片"]').exists()).toBe(true)
    expect(wrapper.findAll('input[type="text"]').length).toBeGreaterThanOrEqual(2)
  })

  // 既有「註冊成功」情境目前沒有自動化元件測試涵蓋（見 6.6）。
  it('提供正確 Email／密碼／驗證碼送出後呼叫註冊 API 成功並導向登入頁', async () => {
    vi.mocked(authApi.register).mockResolvedValue({ id: 'member-1' })
    const wrapper = mountPage()

    await fillAndSubmit(wrapper)

    expect(authApi.register).toHaveBeenCalledWith('user@example.com', 'Password123', 'Alice', 'captcha-token-1', 'TEST')
    expect(pushMock).toHaveBeenCalledWith('/login')
  })

  // CAPTCHA-BW-002：驗證碼錯誤時顯示提示且自動換發。
  it('CAPTCHA-BW-002：驗證碼錯誤時顯示提示且自動換發新驗證碼', async () => {
    vi.mocked(authApi.register).mockRejectedValue(
      new ApiError(400, { status: 400, title: 'CaptchaInvalid', detail: '驗證碼錯誤或已逾時，請重新取得驗證碼。' }),
    )
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
