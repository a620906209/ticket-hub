import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import EventCreatePage from './EventCreatePage.vue'
import * as adminApi from '../../api/admin'
import type { VenueDetail } from '../../types/apiResponses'

vi.mock('../../api/admin')

const pushMock = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: pushMock }),
}))

// ElSelect／ElOption 的真實實作走 popper + teleport + 虛擬清單，在 jsdom 下互動很脆弱；
// 這裡用行為等價的原生 <select>/<option> 取代，只驗證元件本身的邏輯（狀態、API 呼叫時機、
// 過期回應防護），不驗證 Element Plus 本身的下拉選單渲染細節。
const ElSelectStub = {
  props: ['modelValue', 'disabled', 'loading', 'placeholder', 'noDataText'],
  emits: ['update:modelValue', 'change'],
  template: `<select :disabled="disabled" :data-loading="loading"
      :value="modelValue" @change="$emit('update:modelValue', $event.target.value); $emit('change', $event.target.value)">
    <option value="" />
    <slot />
  </select>`,
}
const ElOptionStub = {
  props: ['value', 'label'],
  template: `<option :value="value">{{ label }}</option>`,
}
// ElDatePicker 的日曆彈窗互動在 jsdom 下同樣不好模擬，用一個普通文字輸入取代，
// 只需要能綁定 v-model 讓表單驗證通過即可，不驗證日期選擇器本身的行為。
const ElDatePickerStub = {
  props: ['modelValue'],
  emits: ['update:modelValue'],
  template: `<input :value="modelValue" @input="$emit('update:modelValue', $event.target.value)" />`,
}

function mountPage() {
  return mount(EventCreatePage, {
    global: {
      plugins: [ElementPlus],
      stubs: { ElSelect: ElSelectStub, ElOption: ElOptionStub, ElDatePicker: ElDatePickerStub, RouterLink: true },
    },
  })
}

describe('EventCreatePage 建立活動：場館／座位圖下拉選單', () => {
  beforeEach(() => {
    pushMock.mockReset()
  })

  it('選擇場館後，座位圖下拉選單顯示該場館底下的座位圖選項', async () => {
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: 'venue-a', name: 'Venue A' }])
    vi.mocked(adminApi.getVenueById).mockResolvedValue({
      id: 'venue-a',
      name: 'Venue A',
      seatMaps: [{ id: 'seatmap-1', seatCount: 10 }],
    })
    const wrapper = mountPage()
    await flushPromises()

    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    await venueSelect.setValue('venue-a')
    await flushPromises()

    expect(adminApi.getVenueById).toHaveBeenCalledWith('venue-a')
    expect(seatMapSelect.findAll('option').map((o) => o.attributes('value'))).toContain('seatmap-1')
  })

  it('場館或座位圖尚無可選項目時，欄位為空且無法送出表單', async () => {
    vi.mocked(adminApi.getVenues).mockResolvedValue([])
    const wrapper = mountPage()
    await flushPromises()

    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    expect(venueSelect.findAll('option')).toHaveLength(1) // 只有預設的空白選項
    expect(seatMapSelect.attributes('disabled')).toBeDefined()
  })

  it('已選座位圖後改選另一個場館，座位圖選擇值被清除、下拉選單改顯示新場館的選項', async () => {
    vi.mocked(adminApi.getVenues).mockResolvedValue([
      { id: 'venue-a', name: 'Venue A' },
      { id: 'venue-b', name: 'Venue B' },
    ])
    vi.mocked(adminApi.getVenueById).mockImplementation(async (venueId: string) => {
      if (venueId === 'venue-a') {
        return { id: 'venue-a', name: 'Venue A', seatMaps: [{ id: 'seatmap-a', seatCount: 1 }] }
      }
      return { id: 'venue-b', name: 'Venue B', seatMaps: [{ id: 'seatmap-b', seatCount: 2 }] }
    })
    const wrapper = mountPage()
    await flushPromises()

    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    await venueSelect.setValue('venue-a')
    await flushPromises()
    await seatMapSelect.setValue('seatmap-a')
    expect((seatMapSelect.element as HTMLSelectElement).value).toBe('seatmap-a')

    await venueSelect.setValue('venue-b')
    await flushPromises()

    expect((seatMapSelect.element as HTMLSelectElement).value).toBe('')
    expect(seatMapSelect.findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'seatmap-b'])
  })

  it('快速連續切換兩次場館，較晚抵達但對應較舊選擇的回應不會覆蓋目前的選擇', async () => {
    vi.mocked(adminApi.getVenues).mockResolvedValue([
      { id: 'venue-a', name: 'Venue A' },
      { id: 'venue-b', name: 'Venue B' },
    ])
    let resolveA: (value: VenueDetail) => void
    const venueAPromise = new Promise<VenueDetail>((resolve) => {
      resolveA = resolve
    })
    vi.mocked(adminApi.getVenueById).mockImplementation(async (venueId: string) => {
      if (venueId === 'venue-a') return venueAPromise
      return { id: 'venue-b', name: 'Venue B', seatMaps: [{ id: 'seatmap-b', seatCount: 2 }] }
    })
    const wrapper = mountPage()
    await flushPromises()

    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    await venueSelect.setValue('venue-a') // 觸發但先不 resolve
    await venueSelect.setValue('venue-b') // B 的請求會先 resolve
    await flushPromises()

    // 這時候才讓 A 的（較舊、較晚抵達的）回應完成
    resolveA!({ id: 'venue-a', name: 'Venue A', seatMaps: [{ id: 'seatmap-a', seatCount: 1 }] })
    await flushPromises()

    expect(seatMapSelect.findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'seatmap-b'])
  })

  it('getVenues() 失敗時顯示錯誤訊息', async () => {
    vi.mocked(adminApi.getVenues).mockRejectedValue(new Error('network error'))
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('載入場館列表失敗')
  })

  it('建立活動成功後導向活動列表頁，而不是停留在原頁重置表單', async () => {
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: 'venue-a', name: 'Venue A' }])
    vi.mocked(adminApi.getVenueById).mockResolvedValue({
      id: 'venue-a',
      name: 'Venue A',
      seatMaps: [{ id: 'seatmap-1', seatCount: 10 }],
    })
    vi.mocked(adminApi.createEvent).mockResolvedValue({ id: 'event-1' })
    const wrapper = mountPage()
    await flushPromises()

    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    await venueSelect.setValue('venue-a')
    await flushPromises()
    await seatMapSelect.setValue('seatmap-1')
    await wrapper.find('input[maxlength="200"]').setValue('Concert')
    await wrapper.findComponent(ElDatePickerStub).setValue('2026-12-31T20:00')

    const form = wrapper.find('form')
    await form.trigger('submit')
    await flushPromises()

    expect(pushMock).toHaveBeenCalledWith({ name: 'admin-events' })
  })
})
