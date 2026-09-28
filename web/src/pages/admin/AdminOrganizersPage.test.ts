import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminOrganizersPage from './AdminOrganizersPage.vue'
import * as organizersApi from '../../api/organizers'

vi.mock('../../api/organizers')

function mountPage() {
  return mount(AdminOrganizersPage, { global: { plugins: [ElementPlus] } })
}

function findButtonByText(wrapper: ReturnType<typeof mount>, text: string) {
  return wrapper.findAll('button').find((b) => b.text() === text)
}

describe('AdminOrganizersPage 主辦方審核清單', () => {
  beforeEach(() => {
    vi.mocked(organizersApi.getPendingOrganizers).mockReset()
    vi.mocked(organizersApi.approveOrganizer).mockReset()
    vi.mocked(organizersApi.rejectOrganizer).mockReset()
  })

  // AWU-REVIEW-001
  it('顯示目前所有待審核的主辦方申請與申請人', async () => {
    vi.mocked(organizersApi.getPendingOrganizers).mockResolvedValue([
      { id: 'org-1', name: 'Pending Org', createdByMemberId: 'member-1', createdByDisplayName: 'Alice', createdAtUtc: '2026-01-01T00:00:00Z' },
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('Pending Org')
    expect(wrapper.text()).toContain('Alice')
  })

  // AWU-REVIEW-002
  it('點選核准後呼叫核准端點成功，重新查詢清單，該筆申請自清單移除', async () => {
    vi.mocked(organizersApi.getPendingOrganizers)
      .mockResolvedValueOnce([
        { id: 'org-1', name: 'Pending Org', createdByMemberId: 'member-1', createdByDisplayName: 'Alice', createdAtUtc: '2026-01-01T00:00:00Z' },
      ])
      .mockResolvedValueOnce([])
    vi.mocked(organizersApi.approveOrganizer).mockResolvedValue(undefined)
    const wrapper = mountPage()
    await flushPromises()

    await findButtonByText(wrapper, '核准')!.trigger('click')
    await flushPromises()

    expect(organizersApi.approveOrganizer).toHaveBeenCalledWith('org-1')
    expect(organizersApi.getPendingOrganizers).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Pending Org')
  })

  // AWU-REVIEW-003
  it('點選駁回後呼叫駁回端點成功，重新查詢清單，該筆申請自清單移除', async () => {
    vi.mocked(organizersApi.getPendingOrganizers)
      .mockResolvedValueOnce([
        { id: 'org-1', name: 'Pending Org', createdByMemberId: 'member-1', createdByDisplayName: 'Alice', createdAtUtc: '2026-01-01T00:00:00Z' },
      ])
      .mockResolvedValueOnce([])
    vi.mocked(organizersApi.rejectOrganizer).mockResolvedValue(undefined)
    const wrapper = mountPage()
    await flushPromises()

    await findButtonByText(wrapper, '駁回')!.trigger('click')
    await flushPromises()

    expect(organizersApi.rejectOrganizer).toHaveBeenCalledWith('org-1')
    expect(organizersApi.getPendingOrganizers).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Pending Org')
  })
})
