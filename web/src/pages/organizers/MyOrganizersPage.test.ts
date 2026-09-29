import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import MyOrganizersPage from './MyOrganizersPage.vue'
import * as organizersApi from '../../api/organizers'
import type { OrganizerSummary } from '../../types/apiResponses'

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
  return mount(MyOrganizersPage, {
    global: { plugins: [ElementPlus], stubs: { RouterLink: true } },
  })
}

function findSwitchButtons(wrapper: ReturnType<typeof mount>) {
  return wrapper.findAll('button').filter((b) => b.text() === '切換')
}

describe('MyOrganizersPage 我的主辦方清單', () => {
  beforeEach(() => {
    pushMock.mockReset()
    applySwitchedAccessTokenMock.mockReset()
    getCurrentRefreshTokenMock.mockReset()
    getCurrentRefreshTokenMock.mockReturnValue('current-refresh-token')
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    vi.mocked(organizersApi.switchOrganizerContext).mockReset()
  })

  // AWU-LIST-001
  it('顯示所屬主辦方清單與各自狀態', async () => {
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([
      { id: 'org-1', name: 'Approved Org', status: 'Approved' },
      { id: 'org-2', name: 'Pending Org', status: 'Pending' },
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.text()).toContain('Approved Org')
    expect(wrapper.text()).toContain('Pending Org')
    expect(wrapper.text()).toContain('已核准')
    expect(wrapper.text()).toContain('待審核')
  })

  // AWU-LIST-002
  it('對 Approved 項目點選切換，帶目前的 Refresh Token 呼叫切換端點成功後導向後台首頁', async () => {
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([{ id: 'org-1', name: 'Approved Org', status: 'Approved' }])
    vi.mocked(organizersApi.switchOrganizerContext).mockResolvedValue({ accessToken: 'new-access-token' })
    const wrapper = mountPage()
    await flushPromises()

    const [switchButton] = findSwitchButtons(wrapper)
    expect(switchButton).toBeDefined()
    await switchButton.trigger('click')
    await flushPromises()

    expect(organizersApi.switchOrganizerContext).toHaveBeenCalledWith('org-1', 'current-refresh-token')
    expect(applySwitchedAccessTokenMock).toHaveBeenCalledWith('new-access-token')
    expect(pushMock).toHaveBeenCalledWith('/admin')
  })

  // AWU-LIST-003：Pending／Rejected／Suspended 皆不提供切換操作
  it.each(['Pending', 'Rejected', 'Suspended'])('狀態為 %s 的項目不提供切換操作', async (status) => {
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([
      { id: 'org-1', name: 'Org', status } as OrganizerSummary,
    ])
    const wrapper = mountPage()
    await flushPromises()

    expect(findSwitchButtons(wrapper)).toHaveLength(0)
  })

  // AWU-LIST-004
  it('清單為空時顯示提示文字與前往申請表單的入口，不顯示錯誤', async () => {
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([])
    const wrapper = mountPage()
    await flushPromises()

    expect(wrapper.find('.el-alert').exists()).toBe(false)
    expect(wrapper.text()).toContain('尚未加入任何主辦方')
    expect(wrapper.findComponent({ name: 'RouterLink' }).exists()).toBe(true)
  })
})
