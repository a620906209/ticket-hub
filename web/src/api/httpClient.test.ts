import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  authorizedRequest,
  configureHttpClientAuth,
  configureHttpClientRefresh,
  request,
  requestBlob,
} from './httpClient'

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  })
}

describe('httpClient request', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    configureHttpClientAuth(() => null)
  })

  it('帶入 Authorization Header 當有 access token 時', async () => {
    configureHttpClientAuth(() => 'test-token')
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse(200, { ok: true }))

    await request('/events')

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init?.headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer test-token')
  })

  it('不帶 Authorization Header 當沒有 access token 時', async () => {
    configureHttpClientAuth(() => null)
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse(200, { ok: true }))

    await request('/events')

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init?.headers as Record<string, string>
    expect(headers.Authorization).toBeUndefined()
  })

  it('skipAuth 為 true 時即使有 token 也不帶 Authorization Header', async () => {
    configureHttpClientAuth(() => 'test-token')
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse(200, { ok: true }))

    await request('/auth/login', { method: 'POST', skipAuth: true, body: { email: 'a', password: 'b' } })

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init?.headers as Record<string, string>
    expect(headers.Authorization).toBeUndefined()
  })

  it('非 2xx 回應時，把 ProblemDetails 轉成 ApiError', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(
      jsonResponse(400, { status: 400, title: 'Validation', detail: 'Email 為必填。' }),
    )

    await expect(request('/auth/login', { method: 'POST', skipAuth: true, body: {} })).rejects.toMatchObject({
      status: 400,
      message: 'Email 為必填。',
    })
  })

  it('ApiError 帶有原始 ProblemDetails 物件供畫面判斷用', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(jsonResponse(404, { status: 404, title: 'NotFound', detail: '找不到活動。' }))

    try {
      await request('/events/does-not-exist')
      expect.unreachable('應該要拋出例外')
    } catch (error) {
      expect(error).toBeInstanceOf(ApiError)
      const apiError = error as ApiError
      expect(apiError.problem?.title).toBe('NotFound')
    }
  })

  it('204 No Content 回應時回傳 undefined，不嘗試解析 body', async () => {
    vi.mocked(fetch).mockResolvedValueOnce(new Response(null, { status: 204 }))

    await expect(request('/orders/some-id/confirm', { method: 'POST' })).resolves.toBeUndefined()
  })
})

describe('requestBlob', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    configureHttpClientAuth(() => null)
    configureHttpClientRefresh(() => Promise.resolve(false))
  })

  it('帶入 Authorization Header 並回傳二進位內容', async () => {
    configureHttpClientAuth(() => 'test-token')
    vi.mocked(fetch).mockResolvedValueOnce(new Response('png-data', { status: 200, headers: { 'content-type': 'image/png' } }))

    const blob = await requestBlob('/tickets/ticket-1/qr-code')

    const [, init] = vi.mocked(fetch).mock.calls[0]
    const headers = init?.headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer test-token')
    await expect(blob.text()).resolves.toBe('png-data')
  })

  it('收到 401 後換發並以新 token 重試一次', async () => {
    let currentToken = 'expired-token'
    configureHttpClientAuth(() => currentToken)
    configureHttpClientRefresh(async () => {
      currentToken = 'new-token'
      return true
    })
    vi.mocked(fetch).mockImplementation(async (_url, init) => {
      const headers = init?.headers as Record<string, string>
      return headers.Authorization === 'Bearer new-token'
        ? new Response('png-data', { status: 200 })
        : jsonResponse(401, { status: 401, detail: 'expired' })
    })

    const blob = await requestBlob('/tickets/ticket-1/qr-code')
    await expect(blob.text()).resolves.toBe('png-data')
    expect(fetch).toHaveBeenCalledTimes(2)
  })
})

describe('authorizedRequest single-flight 換發', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn())
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    configureHttpClientAuth(() => null)
    configureHttpClientRefresh(() => Promise.resolve(false))
  })

  it('多個並發請求同時收到 401 時，只呼叫一次 refresh，全部最終都用新 token 重放成功', async () => {
    let currentToken = 'expired-token'
    configureHttpClientAuth(() => currentToken)

    const refreshHandler = vi.fn(async () => {
      await new Promise((resolve) => setTimeout(resolve, 10))
      currentToken = 'new-token'
      return true
    })
    configureHttpClientRefresh(refreshHandler)

    vi.mocked(fetch).mockImplementation(async (_url, init) => {
      const headers = init?.headers as Record<string, string>
      if (headers.Authorization === 'Bearer new-token') {
        return jsonResponse(200, { ok: true })
      }
      return jsonResponse(401, { status: 401, detail: 'expired' })
    })

    const [resultA, resultB] = await Promise.all([authorizedRequest('/events'), authorizedRequest('/orders')])

    expect(resultA).toEqual({ ok: true })
    expect(resultB).toEqual({ ok: true })
    expect(refreshHandler).toHaveBeenCalledTimes(1)
  })

  it('換發後重試仍 401 時不再觸發第二次換發，直接把錯誤丟出去', async () => {
    configureHttpClientAuth(() => 'expired-token')
    const refreshHandler = vi.fn().mockResolvedValue(true)
    configureHttpClientRefresh(refreshHandler)

    vi.mocked(fetch).mockResolvedValue(jsonResponse(401, { status: 401, detail: 'still expired' }))

    await expect(authorizedRequest('/events')).rejects.toMatchObject({ status: 401 })
    expect(refreshHandler).toHaveBeenCalledTimes(1)
  })

  it('換發失敗時直接把原始 401 錯誤丟出去，不重放請求', async () => {
    configureHttpClientAuth(() => 'expired-token')
    const refreshHandler = vi.fn().mockResolvedValue(false)
    configureHttpClientRefresh(refreshHandler)

    vi.mocked(fetch).mockResolvedValue(jsonResponse(401, { status: 401, detail: 'expired' }))

    await expect(authorizedRequest('/events')).rejects.toMatchObject({ status: 401 })
    expect(fetch).toHaveBeenCalledTimes(1)
  })
})
