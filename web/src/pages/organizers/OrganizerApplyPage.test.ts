import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import OrganizerApplyPage from './OrganizerApplyPage.vue'
import * as organizersApi from '../../api/organizers'
import { ApiError } from '../../api/httpClient'

vi.mock('../../api/organizers')

const pushMock = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: pushMock }),
}))

const applySwitchedAccessTokenMock = vi.fn()
const getCurrentRefreshTokenMock = vi.fn()
vi.mock('../../stores/auth', () => ({
  useAuthStore: () => ({
    getCurrentRefreshToken: getCurrentRefreshTokenMock,
    applySwitchedAccessToken: applySwitchedAccessTokenMock,
  }),
}))

function mountPage() {
  return mount(OrganizerApplyPage, {
    global: { plugins: [ElementPlus] },
  })
}

describe('OrganizerApplyPage 申請建立主辦方', () => {
  beforeEach(() => {
    pushMock.mockReset()
    applySwitchedAccessTokenMock.mockReset()
    getCurrentRefreshTokenMock.mockReset()
    getCurrentRefreshTokenMock.mockReturnValue('current-refresh-token')
    vi.mocked(organizersApi.applyForOrganizer).mockReset()
    vi.mocked(organizersApi.switchOrganizerContext).mockReset()
  })

  // AWU-APPLY-001
  it('填寫有效名稱送出，呼叫申請端點成功', async () => {
    vi.mocked(organizersApi.applyForOrganizer).mockResolvedValue({ id: 'org-1' })
    vi.mocked(organizersApi.switchOrganizerContext).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))
    const wrapper = mountPage()

    await wrapper.find('input').setValue('My Organizer')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(organizersApi.applyForOrganizer).toHaveBeenCalledWith('My Organizer')
  })

  // AWU-APPLY-003
  it('名稱留空送出，顯示驗證錯誤，不呼叫申請端點', async () => {
    const wrapper = mountPage()

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(organizersApi.applyForOrganizer).not.toHaveBeenCalled()
  })

  // AWU-APPLY-002：申請成功後自動切換，因 Pending 被拒絕（狀態衝突）
  it('申請成功後自動切換因 Pending 被拒絕，仍顯示已送出並導向清單頁，不顯示錯誤', async () => {
    vi.mocked(organizersApi.applyForOrganizer).mockResolvedValue({ id: 'org-1' })
    vi.mocked(organizersApi.switchOrganizerContext).mockRejectedValue(new ApiError(409, { status: 409, title: 'Conflict' }))
    const wrapper = mountPage()

    await wrapper.find('input').setValue('My Organizer')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(organizersApi.switchOrganizerContext).toHaveBeenCalledWith('org-1', 'current-refresh-token')
    expect(applySwitchedAccessTokenMock).not.toHaveBeenCalled()
    expect(pushMock).toHaveBeenCalledWith({ name: 'my-organizers' })
    expect(wrapper.find('.el-alert').exists()).toBe(false)
  })

  // AWU-APPLY-004：自動切換因非預期原因（網路逾時／5xx）失敗
  it('申請成功後自動切換因非預期錯誤失敗，仍顯示已送出並導向清單頁，不顯示錯誤', async () => {
    vi.mocked(organizersApi.applyForOrganizer).mockResolvedValue({ id: 'org-1' })
    vi.mocked(organizersApi.switchOrganizerContext).mockRejectedValue(new Error('network timeout'))
    const wrapper = mountPage()

    await wrapper.find('input').setValue('My Organizer')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(pushMock).toHaveBeenCalledWith({ name: 'my-organizers' })
    expect(wrapper.find('.el-alert').exists()).toBe(false)
  })
})
