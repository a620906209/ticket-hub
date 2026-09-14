import { describe, expect, it, vi, beforeEach } from 'vitest'
import { useCaptcha } from './useCaptcha'
import * as captchaApi from '../api/captcha'

vi.mock('../api/captcha')

describe('useCaptcha', () => {
  beforeEach(() => {
    vi.mocked(captchaApi.getCaptcha).mockReset()
  })

  it('呼叫 API 成功後正確暴露 token/imageBase64', async () => {
    vi.mocked(captchaApi.getCaptcha).mockResolvedValue({ token: 'token-1', imageBase64: 'base64-1' })
    const { token, imageBase64, refresh } = useCaptcha()

    await refresh()

    expect(token.value).toBe('token-1')
    expect(imageBase64.value).toBe('base64-1')
  })

  it('refresh() 換發新的 token/imageBase64', async () => {
    vi.mocked(captchaApi.getCaptcha)
      .mockResolvedValueOnce({ token: 'token-1', imageBase64: 'base64-1' })
      .mockResolvedValueOnce({ token: 'token-2', imageBase64: 'base64-2' })
    const { token, refresh } = useCaptcha()
    await refresh()

    await refresh()

    expect(token.value).toBe('token-2')
    expect(captchaApi.getCaptcha).toHaveBeenCalledTimes(2)
  })

  // CAPTCHA-BW-005／006：API 呼叫失敗（429／5xx）時設定 loadError 為固定提示文字，不拋出例外中斷呼叫端。
  it('API 呼叫失敗時設定 loadError 為固定提示文字，不拋出例外', async () => {
    vi.mocked(captchaApi.getCaptcha).mockRejectedValue(new Error('network error'))
    const { loadError, refresh } = useCaptcha()

    await expect(refresh()).resolves.toBeUndefined()

    expect(loadError.value).toBe('系統暫時無法取得驗證碼，請稍後再試')
  })

  it('失敗後再次呼叫成功，loadError 清空', async () => {
    vi.mocked(captchaApi.getCaptcha)
      .mockRejectedValueOnce(new Error('network error'))
      .mockResolvedValueOnce({ token: 'token-1', imageBase64: 'base64-1' })
    const { token, loadError, refresh } = useCaptcha()
    await refresh()
    expect(loadError.value).not.toBe('')

    await refresh()

    expect(loadError.value).toBe('')
    expect(token.value).toBe('token-1')
  })
})
