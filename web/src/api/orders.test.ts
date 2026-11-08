import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { getMyOrders } from './orders'

// 頁面測試只 mock 到 api 函式這一層，看不到實際請求網址；這裡驗證 page 確實放進查詢字串。
describe('orders api 分頁請求（order-list-pagination）', () => {
  const fetchMock = vi.fn()
  const envelope = { items: [], page: 2, pageSize: 20, totalCount: 25 }

  beforeEach(() => {
    fetchMock.mockReset().mockImplementation(() =>
      Promise.resolve(new Response(JSON.stringify(envelope), { status: 200, headers: { 'content-type': 'application/json' } })),
    )
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('[BW-MYORDER-PAGE-001] getMyOrders(2) 以 page=2 請求並回傳分頁結構', async () => {
    const result = await getMyOrders(2)

    expect(fetchMock.mock.calls[0][0]).toBe('/api/orders?page=2')
    expect(result).toEqual(envelope)
  })

  it('[BW-MYORDER-LIST-001] getMyOrders() 未指定頁碼時請求第 1 頁', async () => {
    await getMyOrders()

    expect(fetchMock.mock.calls[0][0]).toBe('/api/orders?page=1')
  })
})
