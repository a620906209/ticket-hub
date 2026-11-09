import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createEvent, getAdminEvents, getAdminOrders, getTicketHolder, redeemTicket } from './admin'

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
    await createEvent({
      title: 'Concert',
      startAtUtc: '2026-12-31T12:00:00.000Z',
      venueId: 'venue-1',
      seatMapId: 'seatmap-1',
      isRealNameRequired,
    })

    expect(sentRequest().body).toMatchObject({ isRealNameRequired })
  })

  it('核銷未確認持票人時 body 不帶 isHolderVerified，由後端視為 false', async () => {
    await redeemTicket('ticket-1', 'sig-1')

    const request = sentRequest()
    expect(request.url).toBe('/api/admin/tickets/ticket-1/redeem')
    expect(request.method).toBe('PATCH')
    expect(request.body).toEqual({ signature: 'sig-1' })
  })

  it('核銷確認持票人後 body 帶 isHolderVerified = true 與原簽章', async () => {
    await redeemTicket('ticket-1', 'sig-1', true)

    expect(sentRequest().body).toEqual({ signature: 'sig-1', isHolderVerified: true })
  })

  // 手動輸入路徑沿用既有慣例：signature 送 null 代表「操作人員信任操作」，不是省略欄位（AWU-REDEEM-RN-008）。
  it('[AWU-REDEEM-RN-008] 手動輸入路徑確認持票人後 body 的 signature 為 null 並帶 isHolderVerified = true', async () => {
    await redeemTicket('ticket-1', null, true)

    expect(sentRequest().body).toEqual({ signature: null, isHolderVerified: true })
  })

  it('查詢持票人呼叫 GET /api/admin/tickets/{id}/holder', async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ ticketId: 'ticket-1' }), { status: 200, headers: { 'content-type': 'application/json' } }),
    )

    await getTicketHolder('ticket-1')

    const request = sentRequest()
    expect(request.url).toBe('/api/admin/tickets/ticket-1/holder')
    expect(request.method).toBe('GET')
  })
})

describe('admin api 訂單列表分頁請求（order-list-pagination）', () => {
  const fetchMock = vi.fn()
  const envelope = { items: [], page: 3, pageSize: 20, totalCount: 45 }

  beforeEach(() => {
    fetchMock.mockReset().mockImplementation(() =>
      Promise.resolve(new Response(JSON.stringify(envelope), { status: 200, headers: { 'content-type': 'application/json' } })),
    )
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('[AWU-ORDER-PAGE-001] getAdminOrders(3) 以 page=3 請求並回傳分頁結構', async () => {
    const result = await getAdminOrders(3)

    expect(fetchMock.mock.calls[0][0]).toBe('/api/admin/orders?page=3')
    expect(result).toEqual(envelope)
  })

  it('[AWU-ORDER-LIST-001] getAdminOrders() 未指定頁碼時請求第 1 頁', async () => {
    await getAdminOrders()

    expect(fetchMock.mock.calls[0][0]).toBe('/api/admin/orders?page=1')
  })
})

describe('admin api 活動列表分頁請求（admin-event-list-pagination）', () => {
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

  it('[AWU-EVENT-PAGE-001] getAdminEvents(2) 以 page=2 請求並回傳分頁結構', async () => {
    const result = await getAdminEvents(2)

    expect(fetchMock.mock.calls[0][0]).toBe('/api/admin/events?page=2')
    expect(result).toEqual(envelope)
  })

  it('getAdminEvents() 未指定頁碼時請求第 1 頁', async () => {
    await getAdminEvents()

    expect(fetchMock.mock.calls[0][0]).toBe('/api/admin/events?page=1')
  })
})
