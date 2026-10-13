import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { logout } from './auth'
import { configureHttpClientAuth } from './httpClient'

// 後端登出只憑 refresh token 撤銷（logout-revoke-by-refresh-token design.md 決策 6）：
// 頁面與 store 測試只 mock 到 api 函式這一層，看不到實際送出的 header 與 body。
describe('auth api logout', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    configureHttpClientAuth(() => null)
  })

  // store 內若還殘留 access token 也不能帶出去：用 skipAuth 而不是依賴「store 已清空」這個呼叫端時序。
  it('logout_SendsRequestWithoutAuthorizationHeader', async () => {
    configureHttpClientAuth(() => 'stale-access-token')

    await logout('refresh-1')

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toBe('/api/auth/logout')
    expect(init.method).toBe('POST')
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined()
    expect(JSON.parse(init.body as string)).toEqual({ refreshToken: 'refresh-1' })
  })
})
