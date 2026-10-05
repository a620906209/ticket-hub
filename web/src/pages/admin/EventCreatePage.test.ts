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

describe('EventCreatePage 需實名設定（real-name-verification）', () => {
  beforeEach(() => {
    pushMock.mockReset()
    vi.mocked(adminApi.createEvent).mockReset().mockResolvedValue({ id: 'event-1' })
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: 'venue-a', name: 'Venue A' }])
    vi.mocked(adminApi.getVenueById).mockResolvedValue({
      id: 'venue-a',
      name: 'Venue A',
      seatMaps: [{ id: 'seatmap-1', seatCount: 10 }],
    })
  })

  async function fillRequiredFields(wrapper: ReturnType<typeof mountPage>): Promise<void> {
    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    await venueSelect.setValue('venue-a')
    await flushPromises()
    await seatMapSelect.setValue('seatmap-1')
    await wrapper.find('input[maxlength="200"]').setValue('Concert')
    await wrapper.findComponent(ElDatePickerStub).setValue('2026-12-31T20:00')
  }

  function realNameCheckbox(wrapper: ReturnType<typeof mountPage>) {
    return wrapper.find('input[type="checkbox"][name="isRealNameRequired"]')
  }

  // isRealNameRequired 如何放進請求 body 由 api/admin.test.ts 驗證。
  function sentIsRealNameRequired(): unknown {
    return vi.mocked(adminApi.createEvent).mock.calls[0][0].isRealNameRequired
  }

  it('[AWU-EVENT-RN-003] 進入頁面時需實名勾選框未勾選，旁邊有建立後不可變更的說明', async () => {
    const wrapper = mountPage()
    await flushPromises()

    expect((realNameCheckbox(wrapper).element as HTMLInputElement).checked).toBe(false)
    expect(wrapper.text()).toContain('建立後不可變更；買家需先登記實名才能購票，入場時需核對證件')
  })

  it('[AWU-EVENT-RN-001] 勾選需實名後送出，isRealNameRequired 為 true', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await fillRequiredFields(wrapper)

    await realNameCheckbox(wrapper).setValue(true)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
    expect(sentIsRealNameRequired()).toBe(true)
  })

  it('[AWU-EVENT-RN-002] 未勾選需實名送出，isRealNameRequired 為 false', async () => {
    const wrapper = mountPage()
    await flushPromises()
    await fillRequiredFields(wrapper)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
    expect(sentIsRealNameRequired()).toBe(false)
  })
})

describe('EventCreatePage 販售期間（event-sales-window-web-ui）', () => {
  const START_AT = '2030-06-10T12:00:00Z'
  const SALES_END_ERROR = '停售時間不可晚於活動開始時間'
  const SALES_START_ERROR = '開賣時間須早於停售時間（未填停售時間時為活動開始時間）'

  beforeEach(() => {
    pushMock.mockReset()
    vi.mocked(adminApi.createEvent).mockReset().mockResolvedValue({ id: 'event-1' })
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: 'venue-a', name: 'Venue A' }])
    vi.mocked(adminApi.getVenueById).mockResolvedValue({
      id: 'venue-a',
      name: 'Venue A',
      seatMaps: [{ id: 'seatmap-1', seatCount: 10 }],
    })
  })

  // 欄位順序：開始時間、開賣時間、停售時間
  function datePickers(wrapper: ReturnType<typeof mountPage>) {
    const [startAt, salesStartAt, salesEndAt] = wrapper.findAllComponents(ElDatePickerStub)
    return { startAt, salesStartAt, salesEndAt }
  }

  async function mountWithRequiredFields() {
    const wrapper = mountPage()
    await flushPromises()
    const [venueSelect, seatMapSelect] = wrapper.findAll('select')
    await venueSelect.setValue('venue-a')
    await flushPromises()
    await seatMapSelect.setValue('seatmap-1')
    await wrapper.find('input[maxlength="200"]').setValue('Concert')
    await datePickers(wrapper).startAt.setValue(START_AT)
    return wrapper
  }

  async function submit(wrapper: ReturnType<typeof mountPage>): Promise<void> {
    await wrapper.find('form').trigger('submit')
    await flushPromises()
  }

  function sentInput() {
    return vi.mocked(adminApi.createEvent).mock.calls[0][0]
  }

  function formErrors(wrapper: ReturnType<typeof mountPage>): string[] {
    return wrapper.findAll('.el-form-item__error').map((error) => error.text())
  }

  // Element Plus 欄位錯誤訊息延遲 100ms 才顯示（validateStateDebounced），需等待而非立即斷言。
  async function expectFormErrors(wrapper: ReturnType<typeof mountPage>, expected: string[]): Promise<void> {
    await vi.waitFor(() => expect(formErrors(wrapper)).toEqual(expected))
  }

  it('[AWU-EVENT-SALES-001] 填寫開賣與停售時間後送出，請求內容為帶 Z 的 UTC 字串', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesStartAt.setValue('2030-06-01T01:00:00Z')
    await datePickers(wrapper).salesEndAt.setValue('2030-06-09T02:30:00Z')

    await submit(wrapper)

    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
    expect(sentInput().salesStartAtUtc).toBe('2030-06-01T01:00:00.000Z')
    expect(sentInput().salesEndAtUtc).toBe('2030-06-09T02:30:00.000Z')
  })

  it('[AWU-EVENT-SALES-002] 未填販售期間送出，兩個欄位皆為 undefined', async () => {
    const wrapper = await mountWithRequiredFields()

    await submit(wrapper)

    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
    expect(sentInput().salesStartAtUtc).toBeUndefined()
    expect(sentInput().salesEndAtUtc).toBeUndefined()
  })

  // el-date-picker 清空時 v-model 為 null；若對 null 呼叫 new Date() 會送出 1970-01-01。
  it('[AWU-EVENT-SALES-002] 選了開賣、停售時間後清空（null）送出，仍為 undefined', async () => {
    const wrapper = await mountWithRequiredFields()
    const { salesStartAt, salesEndAt } = datePickers(wrapper)
    await salesStartAt.setValue('2030-06-01T01:00:00Z')
    await salesEndAt.setValue('2030-06-09T02:30:00Z')
    salesStartAt.vm.$emit('update:modelValue', null)
    salesEndAt.vm.$emit('update:modelValue', null)
    await flushPromises()

    await submit(wrapper)

    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
    expect(sentInput().salesStartAtUtc).toBeUndefined()
    expect(sentInput().salesEndAtUtc).toBeUndefined()
  })

  it('[AWU-EVENT-SALES-003] 停售時間晚於活動開始時間，顯示錯誤且不呼叫 API', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesEndAt.setValue('2030-06-10T12:00:01Z')

    await submit(wrapper)

    await expectFormErrors(wrapper, [SALES_END_ERROR])
    expect(adminApi.createEvent).not.toHaveBeenCalled()
  })

  it('[AWU-EVENT-SALES-004] 開賣時間等於停售時間，顯示錯誤且不呼叫 API', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesStartAt.setValue('2030-06-05T00:00:00Z')
    await datePickers(wrapper).salesEndAt.setValue('2030-06-05T00:00:00Z')

    await submit(wrapper)

    await expectFormErrors(wrapper, [SALES_START_ERROR])
    expect(adminApi.createEvent).not.toHaveBeenCalled()
  })

  it('[AWU-EVENT-SALES-004] 開賣時間晚於停售時間，顯示錯誤且不呼叫 API', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesStartAt.setValue('2030-06-06T00:00:00Z')
    await datePickers(wrapper).salesEndAt.setValue('2030-06-05T00:00:00Z')

    await submit(wrapper)

    await expectFormErrors(wrapper, [SALES_START_ERROR])
    expect(adminApi.createEvent).not.toHaveBeenCalled()
  })

  it('[AWU-EVENT-SALES-005] 未填停售時間且開賣時間等於活動開始時間，顯示錯誤且不呼叫 API', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesStartAt.setValue(START_AT)

    await submit(wrapper)

    await expectFormErrors(wrapper, [SALES_START_ERROR])
    expect(adminApi.createEvent).not.toHaveBeenCalled()
  })

  it('[AWU-EVENT-SALES-005] 未填停售時間且開賣時間晚於活動開始時間，顯示錯誤且不呼叫 API', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesStartAt.setValue('2030-06-11T00:00:00Z')

    await submit(wrapper)

    await expectFormErrors(wrapper, [SALES_START_ERROR])
    expect(adminApi.createEvent).not.toHaveBeenCalled()
  })

  it('[AWU-EVENT-SALES-006] 停售時間錯誤顯示後，把活動開始時間改到停售時間之後，錯誤消失並可送出', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesEndAt.setValue('2030-06-11T00:00:00Z')
    await submit(wrapper)
    await expectFormErrors(wrapper, [SALES_END_ERROR])

    await datePickers(wrapper).startAt.setValue('2030-06-12T00:00:00Z')
    await flushPromises()

    await expectFormErrors(wrapper, [])
    await submit(wrapper)
    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
  })

  // 斷言放在再次送出之前：送出會重新驗證全部欄位，無法分辨停售時間變更時是否有重新驗證開賣時間。
  it('[AWU-EVENT-SALES-009] 開賣時間錯誤顯示後，把停售時間改到開賣時間之後，開賣錯誤消失並可送出', async () => {
    const wrapper = await mountWithRequiredFields()
    await datePickers(wrapper).salesStartAt.setValue('2030-06-05T00:00:00Z')
    await datePickers(wrapper).salesEndAt.setValue('2030-06-04T00:00:00Z')
    await submit(wrapper)
    await expectFormErrors(wrapper, [SALES_START_ERROR])

    await datePickers(wrapper).salesEndAt.setValue('2030-06-06T00:00:00Z')
    await flushPromises()

    await expectFormErrors(wrapper, [])
    await submit(wrapper)
    expect(adminApi.createEvent).toHaveBeenCalledTimes(1)
  })

  it('[AWU-EVENT-SALES-007] 開賣與停售時間預設為空，並顯示留空語意與建立後不可變更的說明', async () => {
    const wrapper = mountPage()
    await flushPromises()

    const { salesStartAt, salesEndAt } = datePickers(wrapper)
    expect(salesStartAt.props('modelValue')).toBeFalsy()
    expect(salesEndAt.props('modelValue')).toBeFalsy()
    expect(wrapper.text()).toContain('留空代表建立後立即開賣；建立後不可變更')
    expect(wrapper.text()).toContain('留空代表活動開始時停售；建立後不可變更')
  })
})
