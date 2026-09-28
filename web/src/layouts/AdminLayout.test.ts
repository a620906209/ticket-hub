import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import AdminLayout from './AdminLayout.vue'
import router from '../router/index'
import { useAuthStore } from '../stores/auth'
import * as organizersApi from '../api/organizers'

vi.mock('../api/organizers')
// 場館頁會在 RouterView 內實際渲染並呼叫後台 API，mock 掉避免測試打出真實請求。
vi.mock('../api/admin')

function mountLayout() {
  return mount(AdminLayout, {
    global: { plugins: [ElementPlus, router] },
  })
}

// decodeJwtPayload 只做 base64url decode + JSON.parse，不驗證簽章，見 utils/jwt.ts。
function fakeAccessTokenWithOrganizerId(organizerId: string): string {
  const payloadJson = JSON.stringify({ OrganizerId: organizerId })
  const base64url = btoa(payloadJson).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
  return `header.${base64url}.signature`
}

describe('AdminLayout', () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([])
    const authStore = useAuthStore()
    // 場館頁要求已切換 Organizer（AWU-GUARD-002）；沒帶 OrganizerId claim 會被導到「我的主辦方」頁，
    // 讓測試實際渲染的是那一頁而不是場館頁。
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true }
    await router.push('/admin/venues')
  })

  // 對應 AC: ADMIN-REDEEM-NAV-ENTRY
  it('導覽選單渲染出「票券核銷」項目，且連結指向 /admin/redeem', async () => {
    const wrapper = mountLayout()

    const menuItem = wrapper.findAll('.el-menu-item').find((item) => item.text() === '票券核銷')
    expect(menuItem).toBeDefined()

    await menuItem!.trigger('click')
    await flushPromises()

    // ElMenu 的 router 模式經由 router.push 非同步導覽（含 beforeEach 守衛），單次 flushPromises 在高負載下不保證已完成。
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/admin/redeem'))
  })
})

// AWU-LIST-002：切換成功（OrganizerId claim 更新）後，導覽列依 claim 中的 GUID 比對出並顯示對應名稱。
describe('AdminLayout 導覽列目前 Organizer 名稱顯示', () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    const authStore = useAuthStore()
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true }
    await router.push('/admin/venues')
  })

  it('Access Token 帶 OrganizerId claim 時，依 GET /api/organizers/mine 比對出並顯示對應的主辦方名稱', async () => {
    const authStore = useAuthStore()
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([
      { id: 'org-1', name: '目標主辦方', status: 'Approved' },
      { id: 'org-2', name: '其他主辦方', status: 'Approved' },
    ])
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')

    const wrapper = mountLayout()
    await flushPromises()

    expect(organizersApi.getMyOrganizers).toHaveBeenCalled()
    expect(wrapper.text()).toContain('目標主辦方')
    expect(wrapper.text()).not.toContain('尚未切換主辦方')
  })

  it('尚未切換（Access Token 不帶 OrganizerId claim）時，導覽列顯示提示文字，不呼叫清單端點', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'

    const wrapper = mountLayout()
    await flushPromises()

    expect(organizersApi.getMyOrganizers).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('尚未切換主辦方')
  })
})

// 訂單／核銷／審核頁路由守衛仍要求 Admin 角色（AWU-GUARD-004／006），非 Admin 的 Organizer 成員
// 看到這些選單只會點進去被導回買家首頁，因此選單 MUST 依角色隱藏。
describe('AdminLayout 導覽選單依角色顯示', () => {
  const adminOnlyMenuLabels = ['訂單管理', '票券核銷', '主辦方審核']

  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([])
  })

  it('已切換 Organizer 的非 Admin 成員只看到場館、活動選單', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')
    authStore.member = { id: '2', email: 'member@example.com', displayName: 'Member', role: 'Member', isActive: true }
    await router.push('/admin/venues')

    const wrapper = mountLayout()
    await flushPromises()

    const labels = wrapper.find('.admin-nav-menu').findAll('.el-menu-item').map((item) => item.text())
    expect(labels).toEqual(['場館管理', '活動管理'])
  })

  it('Admin 角色看得到訂單、核銷、審核選單', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true }
    await router.push('/admin/venues')

    const wrapper = mountLayout()
    await flushPromises()

    const labels = wrapper.find('.admin-nav-menu').findAll('.el-menu-item').map((item) => item.text())
    expect(labels).toEqual(expect.arrayContaining(adminOnlyMenuLabels))
  })
})
