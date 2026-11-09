import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getEvents } from './events'

// 頁面測試只 mock 到 api 函式這一層，看不到實際請求；這裡驗證分頁參數真的進入 URL、回應保留分頁封包。
describe('events api 公開活動列表分頁（public-event-list-pagination）', () => {
  const fetchMock = vi.fn()
  const pagedBody = { items: [], page: 2, pageSize: 20, totalCount: 45 }

  beforeEach(() => {
    fetchMock.mockReset().mockResolvedValue(
      new Response(JSON.stringify(pagedBody), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    )
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('getEvents_WithPage_RequestsThatPageAndReturnsEnvelope', async () => {
    const result = await getEvents(2)

    expect(fetchMock.mock.calls[0][0]).toBe('/api/events?page=2')
    expect(result).toEqual(pagedBody)
  })

  it('getEvents_WithoutPage_RequestsFirstPage', async () => {
    await getEvents()

    expect(fetchMock.mock.calls[0][0]).toBe('/api/events?page=1')
  })
})
