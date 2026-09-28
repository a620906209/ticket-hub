import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminOrganizersPage from './AdminOrganizersPage.vue'
import * as organizersApi from '../../api/organizers'

vi.mock('../../api/organizers')

function mountPage() {
  return mount(AdminOrganizersPage, { global: { plugins: [ElementPlus] } })
}

function findButtonsByText(wrapper: ReturnType<typeof mount>, text: string) {
  return wrapper.findAll('button').filter((b) => b.text() === text)
}

const pendingOrg = { id: 'org-1', name: 'Pending Org', createdByMemberId: 'member-1', createdByDisplayName: 'Alice', createdAtUtc: '2026-01-01T00:00:00Z' }
const otherPendingOrg = { id: 'org-2', name: 'Other Org', createdByMemberId: 'member-2', createdByDisplayName: 'Bob', createdAtUtc: '2026-01-02T00:00:00Z' }

describe('AdminOrganizersPage 主辦方審核清單', () => {
  beforeEach(() => {
    vi.mocked(organizersApi.getPendingOrganizers).mockReset()
    vi.mocked(organizersApi.approveOrganizer).mockReset()
    vi.mocked(organizersApi.rejectOrganizer).mockReset()
  })

  it('[AWU-REVIEW-001] 顯示目前所有待審核的主辦方申請與申請人', async () => {
    vi.mocked(organizersApi.getPendingOrganizers).mockResolvedValue([pendingOrg, otherPendingOrg])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('Pending Org')
    expect(wrapper.text()).toContain('Alice')
    expect(wrapper.text()).toContain('Other Org')
    expect(wrapper.text()).toContain('Bob')
  })

  it('[AWU-REVIEW-002] 點選核准後呼叫核准端點成功，重新查詢清單，該筆申請自清單移除', async () => {
    vi.mocked(organizersApi.getPendingOrganizers)
      .mockResolvedValueOnce([pendingOrg, otherPendingOrg])
      .mockResolvedValueOnce([otherPendingOrg])
    vi.mocked(organizersApi.approveOrganizer).mockResolvedValue(undefined)
    const wrapper = mountPage()
    await flushPromises()

    await findButtonsByText(wrapper, '核准')[0].trigger('click')
    await flushPromises()

    expect(organizersApi.approveOrganizer).toHaveBeenCalledTimes(1)
    expect(organizersApi.approveOrganizer).toHaveBeenCalledWith('org-1')
    expect(organizersApi.getPendingOrganizers).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Pending Org')
    expect(wrapper.text()).toContain('Other Org')
  })

  it('[AWU-REVIEW-003] 點選駁回後呼叫駁回端點成功，重新查詢清單，該筆申請自清單移除', async () => {
    vi.mocked(organizersApi.getPendingOrganizers)
      .mockResolvedValueOnce([pendingOrg, otherPendingOrg])
      .mockResolvedValueOnce([otherPendingOrg])
    vi.mocked(organizersApi.rejectOrganizer).mockResolvedValue(undefined)
    const wrapper = mountPage()
    await flushPromises()

    await findButtonsByText(wrapper, '駁回')[0].trigger('click')
    await flushPromises()

    expect(organizersApi.rejectOrganizer).toHaveBeenCalledTimes(1)
    expect(organizersApi.rejectOrganizer).toHaveBeenCalledWith('org-1')
    // 避免兩個按鈕綁錯：駁回不得觸發核准端點。
    expect(organizersApi.approveOrganizer).not.toHaveBeenCalled()
    expect(organizersApi.getPendingOrganizers).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Pending Org')
    expect(wrapper.text()).toContain('Other Org')
  })
})
