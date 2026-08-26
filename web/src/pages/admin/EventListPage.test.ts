import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import EventListPage from './EventListPage.vue'
import * as adminApi from '../../api/admin'
import * as eventsApi from '../../api/events'
import type { AdminEventSummary, TicketType } from '../../types/apiResponses'

vi.mock('../../api/admin')
vi.mock('../../api/events')

function buildEvent(overrides: Partial<AdminEventSummary> = {}): AdminEventSummary {
  return {
    id: 'event-1',
    title: 'Concert',
    startAtUtc: '2026-12-31T12:00:00Z',
    venueId: 'venue-1',
    seatMapId: 'seatmap-1',
    description: null,
    posterUrl: null,
    maxTicketsPerOrder: null,
    createdByMemberId: 'member-1',
    createdByDisplayName: 'Admin A',
    createdAtUtc: '2026-08-19T03:00:00Z',
    availableSeatCount: 0,
    heldSeatCount: 0,
    soldSeatCount: 0,
    ...overrides,
  }
}

function buildTicketType(overrides: Partial<TicketType> = {}): TicketType {
  return {
    id: 'ticket-type-1',
    zoneCode: 'A',
    price: 100,
    requiresSeat: true,
    availableQuantity: null,
    ...overrides,
  }
}

function mountPage() {
  return mount(EventListPage, { global: { plugins: [ElementPlus], stubs: { RouterLink: true } } })
}

// ElInputNumber／ElSwitch 的真實實作在 jsdom 下互動較繁瑣（格式化、blur 提交時機），
// 這裡用行為等價的原生 checkbox/number input 取代，只驗證元件邏輯本身，比照
// EventCreatePage.test.ts 對 ElSelect／ElDatePicker 的既有取捨。
const ElSwitchStub = {
  props: ['modelValue'],
  emits: ['update:modelValue', 'change'],
  template: `<input type="checkbox" role="switch" :checked="modelValue"
      @change="$emit('update:modelValue', $event.target.checked); $emit('change', $event.target.checked)" />`,
}
const ElInputNumberStub = {
  props: ['modelValue', 'disabled'],
  emits: ['update:modelValue', 'change'],
  // 用 @input（而非 @change）比照 vue-test-utils 的 setValue() 對 <input type="number"> 觸發的事件。
  template: `<input type="number" :value="modelValue" :disabled="disabled"
      @input="$emit('update:modelValue', $event.target.valueAsNumber); $emit('change', $event.target.valueAsNumber)" />`,
}
// 「活動」下拉選單同樣改用原生 select，比照 EventCreatePage.test.ts 對 ElSelect 的既有取捨。
const ElSelectStub = {
  props: ['modelValue'],
  emits: ['update:modelValue', 'change'],
  template: `<select :value="modelValue" @change="$emit('update:modelValue', $event.target.value); $emit('change', $event.target.value)">
    <option value="" />
    <slot />
  </select>`,
}
const ElOptionStub = {
  props: ['value', 'label'],
  template: `<option :value="value">{{ label }}</option>`,
}

function mountPageForTicketTypeForm() {
  return mount(EventListPage, {
    global: {
      plugins: [ElementPlus],
      stubs: {
        RouterLink: true,
        ElSwitch: ElSwitchStub,
        ElInputNumber: ElInputNumberStub,
        ElSelect: ElSelectStub,
        ElOption: ElOptionStub,
      },
    },
  })
}

describe('EventListPage 活動列表：建立者/建立時間/售票狀況', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getAdminEvents).mockReset()
  })

  it('建立者為 null 時顯示「—」，不顯示空白', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([buildEvent({ createdByMemberId: null, createdByDisplayName: null })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('—')
  })

  it('建立時間為 null 時顯示「—」，不會被當成 1970/1/1 的假日期', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([buildEvent({ createdAtUtc: null })])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).not.toContain('1970')
    expect(wrapper.text()).toContain('—')
  })

  it('售票狀況橫條圖依三個數字的比例設定各區段的 flex 寬度', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([
      buildEvent({ availableSeatCount: 3, heldSeatCount: 1, soldSeatCount: 6 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    const available = wrapper.find('.seat-status-segment.available')
    const held = wrapper.find('.seat-status-segment.held')
    const sold = wrapper.find('.seat-status-segment.sold')
    expect(available.attributes('style')).toContain('flex: 3')
    expect(held.attributes('style')).toContain('flex: 1')
    expect(sold.attributes('style')).toContain('flex: 6')
  })

  it('總座位數為 0 時顯示「尚無座位資料」，不渲染橫條、不會除以零', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([
      buildEvent({ availableSeatCount: 0, heldSeatCount: 0, soldSeatCount: 0 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('尚無座位資料')
    expect(wrapper.find('.seat-status-bar').exists()).toBe(false)
  })

  it('某個狀態的座位數為 0 時，該狀態不渲染對應的橫條區段', async () => {
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([
      buildEvent({ availableSeatCount: 5, heldSeatCount: 0, soldSeatCount: 0 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.seat-status-segment.available').exists()).toBe(true)
    expect(wrapper.find('.seat-status-segment.held').exists()).toBe(false)
    expect(wrapper.find('.seat-status-segment.sold').exists()).toBe(false)
  })
})

describe('EventListPage 建立票種：座位制／計數制（RequiresSeat 開關）', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getAdminEvents).mockReset()
    vi.mocked(adminApi.createTicketType).mockReset()
    vi.mocked(eventsApi.getTicketTypes).mockReset()
    vi.mocked(adminApi.getAdminEvents).mockResolvedValue([buildEvent()])
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([])
  })

  it('維持「是否綁座位」開關為開啟，建立票種送出 RequiresSeat = true', async () => {
    vi.mocked(adminApi.createTicketType).mockResolvedValue({ id: 'tt-1' })
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[maxlength="50"]').setValue('A')
    await wrapper.find('input[type="number"]').setValue(100)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).toHaveBeenCalledWith('event-1', 'A', 100, true, undefined)
  })

  it('關閉開關並填寫票種名稱、票價、可售總量，建立票種送出 RequiresSeat = false 與 AvailableQuantity', async () => {
    vi.mocked(adminApi.createTicketType).mockResolvedValue({ id: 'tt-1' })
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const numberInputs = wrapper.findAll('input[type="number"]')
    await numberInputs[0].setValue(500)
    await numberInputs[1].setValue(200)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).toHaveBeenCalledWith('event-1', '站立區', 500, false, 200)
  })

  it('關閉開關但可售總量留空，顯示驗證錯誤、不呼叫 createTicketType', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const priceInput = wrapper.findAll('input[type="number"]')[0]
    await priceInput.setValue(500)
    // 可售總量刻意不填（維持 undefined），直接送出。
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('可售總量須為大於 0 的整數')
  })

  it('關閉開關但可售總量填 0，顯示驗證錯誤、不呼叫 createTicketType', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const numberInputs = wrapper.findAll('input[type="number"]')
    await numberInputs[0].setValue(500)
    await numberInputs[1].setValue(0)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('可售總量須為大於 0 的整數')
  })

  it('關閉開關但可售總量填負數，顯示驗證錯誤、不呼叫 createTicketType', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    await wrapper.find('select').setValue('event-1')
    await wrapper.find('input[type="checkbox"]').setValue(false)
    await wrapper.find('input[maxlength="50"]').setValue('站立區')
    const numberInputs = wrapper.findAll('input[type="number"]')
    await numberInputs[0].setValue(500)
    await numberInputs[1].setValue(-1)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createTicketType).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('可售總量須為大於 0 的整數')
  })

  it('關閉開關輸入可售總量後重新開啟開關，可售總量欄位值被清空', async () => {
    const wrapper = mountPageForTicketTypeForm()
    await flushPromises()

    const checkbox = wrapper.find('input[type="checkbox"]')
    await checkbox.setValue(false)
    await wrapper.findAll('input[type="number"]')[1].setValue(200)
    await checkbox.setValue(true)
    await checkbox.setValue(false) // 再次關閉，讓可售總量欄位重新出現以便檢查目前值

    const availableQuantityInput = wrapper.findAll('input[type="number"]')[1]
    expect((availableQuantityInput.element as HTMLInputElement).value).toBe('')
  })

  it('開啟活動列表頁展開票種清單，正確顯示既有票種的模式與可售總量', async () => {
    vi.mocked(eventsApi.getTicketTypes).mockResolvedValue([
      buildTicketType({ id: 'tt-seat', zoneCode: 'A', requiresSeat: true, availableQuantity: null }),
      buildTicketType({ id: 'tt-count', zoneCode: '站立區', requiresSeat: false, availableQuantity: 300 }),
    ])
    const wrapper = mountPage()
    await flushPromises()

    await wrapper.find('.el-table__expand-icon').trigger('click')
    await flushPromises()

    expect(eventsApi.getTicketTypes).toHaveBeenCalledWith('event-1')
    expect(wrapper.text()).toContain('座位制')
    expect(wrapper.text()).toContain('計數制')
    expect(wrapper.text()).toContain('300')
  })
})
