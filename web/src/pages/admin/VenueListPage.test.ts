import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import VenueListPage from './VenueListPage.vue'
import * as adminApi from '../../api/admin'

vi.mock('../../api/admin')

describe('VenueListPage 座位圖摘要', () => {
  beforeEach(() => {
    vi.mocked(adminApi.getVenues).mockReset()
    vi.mocked(adminApi.getVenueById).mockReset()
  })

  // [AWU-SEATMAP-LABEL-001] 與 EventCreatePage.test.ts 同一組 fixture：後端沒有排序，兩頁須以同一規則編號，
  // 管理頁的「座位圖 2」才會是建立活動頁的「座位圖 2」；管理頁仍保留完整 Id 供對照。
  it('[AWU-SEATMAP-LABEL-001] selectVenue_WhenApiReturnsUnsortedSeatMaps_ShowsSameLabelsAsCreatePage', async () => {
    const seatMaps = [
      { id: 'f0e1d2c3-0000-4000-8000-000000000002', seatCount: 120 },
      { id: '0a1b2c3d-0000-4000-8000-000000000001', seatCount: 40 },
    ]
    vi.mocked(adminApi.getVenues).mockResolvedValue([{ id: 'venue-a', name: 'Venue A' }])
    vi.mocked(adminApi.getVenueById).mockResolvedValue({ id: 'venue-a', name: 'Venue A', seatMaps })
    const wrapper = mount(VenueListPage, { global: { plugins: [ElementPlus] } })
    await flushPromises()

    await wrapper.find('.el-table__row').trigger('click')
    await flushPromises()

    const rows = wrapper.find('.seat-map-summary').findAll('.el-table__row')
    const cellTexts = rows.map((row) => row.findAll('td').map((cell) => cell.text()).filter((text) => text !== ''))
    expect(cellTexts).toEqual([
      ['座位圖 1（40 個座位）', '0a1b2c3d-0000-4000-8000-000000000001', '40'],
      ['座位圖 2（120 個座位）', 'f0e1d2c3-0000-4000-8000-000000000002', '120'],
    ])
  })
})
