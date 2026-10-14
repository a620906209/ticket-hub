import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminOrderListPage from './AdminOrderListPage.vue'
import * as adminApi from '../../api/admin'
import type { OrderSummary } from '../../types/apiResponses'

vi.mock('../../api/admin')

function mountPage() {
  return mount(AdminOrderListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: { props: ['to'], template: '<a :href="to"><slot /></a>' } } } })
}

function createOrder(overrides: Partial<OrderSummary>): OrderSummary {
  return {
    id: '00000000-0000-4000-8000-000000000000',
    eventId: 'event-1',
    eventTitle: 'Test Event',
    buyerId: 'buyer-guid-1',
    buyerDisplayName: 'Buyer 1',
    ticketCount: 1,
    totalAmount: 500,
    status: 'Pending',
    createdAtUtc: '2026-09-30T09:50:00Z',
    heldUntilUtc: '2026-09-30T10:00:00Z',
    ...overrides,
  }
}

// 以表頭文字定位欄索引，不假設欄位順序；找不到表頭即失敗，避免 selector 失效時比對空字串而假通過。
function findColumnIndex(wrapper: VueWrapper, header: string): number {
  const headers = wrapper.findAll('.el-table__header th').map((th) => th.text())
  const index = headers.indexOf(header)
  expect(index, `表頭「${header}」應存在，實際表頭：${headers.join('、')}`).toBeGreaterThanOrEqual(0)
  return index
}

// 以訂單短碼定位列，再取指定表頭的 cell；不以「整列文字含某字串」斷言（短碼與時間字串可能含相同數字）。
function findCellText(wrapper: VueWrapper, shortId: string, header: string): string {
  const idColumnIndex = findColumnIndex(wrapper, '訂單編號')
  const row = wrapper.findAll('.el-table__body tr').find((tr) => tr.findAll('td')[idColumnIndex]?.text() === shortId)
  expect(row, `應有訂單編號為 ${shortId} 的列`).toBeDefined()
  return row!.findAll('td')[findColumnIndex(wrapper, header)].text()
}

function readShortIdsInOrder(wrapper: VueWrapper): string[] {
  const idColumnIndex = findColumnIndex(wrapper, '訂單編號')
  return wrapper.findAll('.el-table__body tr').map((tr) => tr.findAll('td')[idColumnIndex].text())
}

describe('AdminOrderListPage 後台訂單列表', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getAdminOrders).mockReset()
    vi.mocked(adminApi.getAdminOrders).mockResolvedValue([
      createOrder({ id: 'pending-order', buyerId: 'buyer-guid-1', buyerDisplayName: 'Buyer 1', status: 'Pending' }),
      createOrder({
        id: 'paid-order',
        eventId: 'event-2',
        buyerId: 'buyer-guid-2',
        buyerDisplayName: 'Buyer 2',
        status: 'Paid',
        createdAtUtc: '2026-09-30T10:50:00Z',
        heldUntilUtc: '2026-09-30T11:00:00Z',
      }),
    ])
  })

  it('[AWU-ORDER-LIST-001] 訂單狀態以中文標籤顯示，Pending／Paid 分別為警示色／成功色', async () => {
    const wrapper = mountPage()
    await flushPromises()

    const rows = wrapper.findAll('.el-table__body tr')
    expect(rows).toHaveLength(2)
    const pendingTag = rows[0].find('.el-tag')
    const paidTag = rows[1].find('.el-tag')
    expect(pendingTag.text()).toBe('待付款')
    expect(pendingTag.classes()).toContain('el-tag--warning')
    expect(paidTag.text()).toBe('已付款')
    expect(paidTag.classes()).toContain('el-tag--success')
    expect(rows[0].text()).toContain('Buyer 1')
    expect(rows[1].text()).toContain('Buyer 2')
    expect(wrapper.text()).not.toContain('buyer-guid-1')
    expect(wrapper.text()).not.toContain('buyer-guid-2')
  })

  // DisplayName 由會員自行設定、活動名稱由主辦方輸入，都會被看到，MUST 以文字渲染、不得產生元素（design.md 安全確認-前端）。
  it('[AWU-ORDER-LIST-001] 買家顯示名稱與活動名稱含 HTML 標籤時原樣以文字顯示，表格內不產生元素', async () => {
    vi.mocked(adminApi.getAdminOrders).mockResolvedValue([
      createOrder({
        id: 'order-1',
        eventTitle: '<img src=x onerror=alert(2)>',
        buyerDisplayName: '<img src=x onerror=alert(1)>',
        status: 'Paid',
      }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.text()).toContain('<img src=x onerror=alert(2)>')
    expect(wrapper.find('.el-table__body img').exists()).toBe(false)
  })

  it('[AWU-ORDER-LIST-002] 顯示訂單編號短碼、活動、張數、總金額與建立時間，明細連結使用完整 Id', async () => {
    const orderId = '3f2a9c1e-0000-4000-8000-000000000001'
    const createdAtUtc = '2026-10-01T03:04:05Z'
    vi.mocked(adminApi.getAdminOrders).mockResolvedValue([
      createOrder({ id: orderId, eventTitle: '春季音樂會', ticketCount: 2, totalAmount: 2500, status: 'Paid', createdAtUtc }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.findAll('.el-table__body tr')).toHaveLength(1)
    expect(findCellText(wrapper, '3f2a9c1e', '訂單編號')).toBe('3f2a9c1e')
    expect(findCellText(wrapper, '3f2a9c1e', '活動')).toBe('春季音樂會')
    expect(findCellText(wrapper, '3f2a9c1e', '張數')).toBe('2')
    expect(findCellText(wrapper, '3f2a9c1e', '總金額')).toBe('NT$2,500')
    expect(findCellText(wrapper, '3f2a9c1e', '建立時間')).toBe(new Date(createdAtUtc).toLocaleString())
    expect(wrapper.find('.el-table__body a').attributes('href')).toBe(`/admin/orders/${orderId}`)
  })

  // HeldUntilUtc 是建立訂單當下的原始值、不因終態改寫，對非 Pending 訂單顯示會誤導成仍在保留中；以「—」代替留白。
  it('[AWU-ORDER-HOLD-001] 持有到期時間只對 Pending 訂單顯示，其餘狀態顯示「—」', async () => {
    vi.mocked(adminApi.getAdminOrders).mockResolvedValue([
      createOrder({ id: 'aaaa0001-0000-4000-8000-000000000000', status: 'Pending', heldUntilUtc: '2026-09-30T10:00:00Z' }),
      createOrder({ id: 'bbbb0002-0000-4000-8000-000000000000', status: 'Paid', heldUntilUtc: '2026-09-30T11:00:00Z' }),
      createOrder({ id: 'cccc0003-0000-4000-8000-000000000000', status: 'Cancelled', heldUntilUtc: '2026-09-30T12:00:00Z' }),
      createOrder({ id: 'dddd0004-0000-4000-8000-000000000000', status: 'Expired', heldUntilUtc: '2026-09-30T13:00:00Z' }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.findAll('.el-table__body tr')).toHaveLength(4)
    expect(findCellText(wrapper, 'aaaa0001', '持有到期時間')).toBe(new Date('2026-09-30T10:00:00Z').toLocaleString())
    expect(findCellText(wrapper, 'bbbb0002', '持有到期時間')).toBe('—')
    expect(findCellText(wrapper, 'cccc0003', '持有到期時間')).toBe('—')
    expect(findCellText(wrapper, 'dddd0004', '持有到期時間')).toBe('—')
  })

  // 排序由後端決定（ORD-LIST-006／008），前端重排會讓同一時間建立的訂單順序與 API 不一致。
  it('[AWU-ORDER-LIST-003] 依 API 回傳順序顯示，前端不重新排序', async () => {
    vi.mocked(adminApi.getAdminOrders).mockResolvedValue([
      createOrder({ id: 'aaaa1111-0000-4000-8000-000000000000', createdAtUtc: '2026-09-30T10:00:00Z' }),
      createOrder({ id: 'bbbb2222-0000-4000-8000-000000000000', createdAtUtc: '2026-09-30T11:00:00Z' }),
      createOrder({ id: 'cccc3333-0000-4000-8000-000000000000', createdAtUtc: '2026-09-30T09:00:00Z' }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(readShortIdsInOrder(wrapper)).toEqual(['aaaa1111', 'bbbb2222', 'cccc3333'])
  })
})
