import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import AdminEventListPage from './admin/EventListPage.vue'
import BuyerEventListPage from './buyer/EventListPage.vue'
import * as adminApi from '../api/admin'
import * as eventsApi from '../api/events'
import type { AdminEventSummary, EventSummary } from '../types/apiResponses'

vi.mock('../api/admin')
vi.mock('../api/events')

// 其他頁面測試都在 global.plugins 安裝 ElementPlus，會掩蓋編譯期按需引入漏轉換的元件；
// 這裡刻意不裝，證明 <el-*> 與 v-loading 由 unplugin-vue-components 在編譯期解析（BS-012）。

type Deferred<T> = { promise: Promise<T>; resolve: (value: T) => void; reject: (reason: unknown) => void }

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((onResolve, onReject) => {
    resolve = onResolve
    reject = onReject
  })
  return { promise, resolve, reject }
}

function buildAdminEvent(overrides: Partial<AdminEventSummary> = {}): AdminEventSummary {
  return {
    id: 'event-1',
    title: 'Concert',
    startAtUtc: '2030-06-01T12:00:00Z',
    venueId: 'venue-1',
    seatMapId: 'seatmap-1',
    description: null,
    posterUrl: null,
    maxTicketsPerOrder: null,
    createdByMemberId: 'member-1',
    createdByDisplayName: 'Admin A',
    createdAtUtc: '2026-08-19T03:00:00Z',
    availableSeatCount: 1,
    heldSeatCount: 0,
    soldSeatCount: 0,
    isRealNameRequired: true,
    salesStartAtUtc: null,
    salesEndAtUtc: null,
    ...overrides,
  }
}

// 後台頁含 <router-link>；以保留 slot 的 stub 取代，避免非 Element Plus 的 resolve warning 造成誤判
const RouterLinkStub = { props: ['to'], template: '<a><slot /></a>' }

function mountWithoutElementPlus(component: typeof AdminEventListPage | typeof BuyerEventListPage): VueWrapper {
  return mount(component, { global: { plugins: [], stubs: { RouterLink: RouterLinkStub } } })
}

function getResolveWarnings(warnSpy: MockInstance): string[] {
  return warnSpy.mock.calls
    .map((args) => args.map(String).join(' '))
    .filter((message) => message.includes('Failed to resolve component') || message.includes('Failed to resolve directive'))
}

describe('[BS-012] 不安裝 Element Plus 全域 plugin 時頁面仍能解析所有元件', () => {
  let warnSpy: MockInstance

  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(adminApi.getAdminEvents).mockReset()
    vi.mocked(eventsApi.getEvents).mockReset()
    warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
  })

  afterEach(() => {
    warnSpy.mockRestore()
  })

  it('後台活動列表：載入中顯示 loading mask，載入後渲染表格、表單元件與實名標籤', async () => {
    const deferred = createDeferred<AdminEventSummary[]>()
    vi.mocked(adminApi.getAdminEvents).mockReturnValue(deferred.promise)
    const wrapper = mountWithoutElementPlus(AdminEventListPage)
    await flushPromises()

    expect.soft(wrapper.find('.el-loading-mask').exists()).toBe(true)

    deferred.resolve([buildAdminEvent()])
    await flushPromises()

    for (const selector of ['.el-table', '.el-select', '.el-switch', '.el-input', '.el-input-number', '.el-form', '.el-button', '.el-tag']) {
      expect.soft(wrapper.find(selector).exists(), selector).toBe(true)
    }
    expect(getResolveWarnings(warnSpy)).toEqual([])
  })

  it('後台活動列表：API 失敗時顯示 el-alert', async () => {
    vi.mocked(adminApi.getAdminEvents).mockRejectedValue(new Error('boom'))
    const wrapper = mountWithoutElementPlus(AdminEventListPage)
    await flushPromises()

    expect.soft(wrapper.find('.el-alert').exists()).toBe(true)
    expect(getResolveWarnings(warnSpy)).toEqual([])
  })

  it('買家首頁：載入中顯示 loading mask', async () => {
    vi.mocked(eventsApi.getEvents).mockReturnValue(createDeferred<EventSummary[]>().promise)
    const wrapper = mountWithoutElementPlus(BuyerEventListPage)
    await flushPromises()

    expect.soft(wrapper.find('.el-loading-mask').exists()).toBe(true)
    expect(getResolveWarnings(warnSpy)).toEqual([])
  })

  it('買家首頁：沒有活動時顯示 el-empty', async () => {
    vi.mocked(eventsApi.getEvents).mockResolvedValue([])
    const wrapper = mountWithoutElementPlus(BuyerEventListPage)
    await flushPromises()

    expect.soft(wrapper.find('.el-empty').exists()).toBe(true)
    expect(getResolveWarnings(warnSpy)).toEqual([])
  })

  it('買家首頁：API 失敗時顯示 el-alert', async () => {
    vi.mocked(eventsApi.getEvents).mockRejectedValue(new Error('boom'))
    const wrapper = mountWithoutElementPlus(BuyerEventListPage)
    await flushPromises()

    expect.soft(wrapper.find('.el-alert').exists()).toBe(true)
    expect(getResolveWarnings(warnSpy)).toEqual([])
  })
})
