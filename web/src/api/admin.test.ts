import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createEvent } from './admin'

// 驗證實名相關參數實際放進請求 body 的形狀：頁面測試只 mock 到 api 函式這一層，看不到 body。
describe('admin api 實名相關請求（real-name-verification）', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockReset().mockResolvedValue(new Response(null, { status: 204 }))
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  function sentRequest(): { url: string; method: string; body: Record<string, unknown> | undefined } {
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    return {
      url,
      method: init.method ?? 'GET',
      body: init.body === undefined ? undefined : (JSON.parse(init.body as string) as Record<string, unknown>),
    }
  }

  it.each([true, false])('[AWU-EVENT-RN-001/002] 建立活動的 body 帶 isRealNameRequired = %s', async (isRealNameRequired) => {
    await createEvent('Concert', '2026-12-31T12:00:00.000Z', 'venue-1', 'seatmap-1', undefined, undefined, undefined, isRealNameRequired)

    expect(sentRequest().body).toMatchObject({ isRealNameRequired })
  })
})
