import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { RouterView } from 'vue-router'
import router from '../router/index'
import { useAuthStore } from '../stores/auth'
import * as organizersApi from '../api/organizers'
import * as eventsApi from '../api/events'
import type { MemberProfile } from '../types/apiResponses'

vi.mock('../api/organizers')
// 買家端首頁（活動列表）會在 RouterView 內實際渲染並呼叫 API，mock 掉避免打出真實請求。
vi.mock('../api/events')

// decodeJwtPayload 只做 base64url decode + JSON.parse，不驗證簽章，見 utils/jwt.ts。
// 刻意不帶 OrganizerId claim：驗證入口不要求已切換 Organizer（BW-ADMIN-ENTRY-001）。
const ACCESS_TOKEN_WITHOUT_ORGANIZER = `header.${btoa(JSON.stringify({ sub: '1' }))}.signature`

let wrapper: VueWrapper | null = null

async function mountAtHomeAs(role: MemberProfile['role']): Promise<VueWrapper> {
  const authStore = useAuthStore()
  authStore.accessToken = ACCESS_TOKEN_WITHOUT_ORGANIZER
  authStore.member = { id: '1', email: 'user@example.com', displayName: '會員選單', role, isActive: true }
  await router.push('/')
  // el-dropdown 選單以 Teleport 渲染到 body，必須掛到 document 上才找得到。
  wrapper = mount(RouterView, { global: { plugins: [ElementPlus, router] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

// el-dropdown 預設以 hover 開啟且選單內容延遲渲染；不先開啟就斷言「看不到」會空洞通過。
async function openMemberDropdown(layout: VueWrapper): Promise<void> {
  await layout.find('.member-trigger').trigger('mouseenter')
  await vi.waitFor(() => expect(findMenuItem('登出')).toBeDefined())
}

function findMenuItem(text: string): HTMLElement | undefined {
  return Array.from(document.body.querySelectorAll<HTMLElement>('.el-dropdown-menu__item')).find(
    (item) => item.textContent?.trim() === text,
  )
}

describe('BuyerLayout 會員下拉選單的主辦方審核入口', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(eventsApi.getEvents).mockReset().mockResolvedValue([])
    vi.mocked(organizersApi.getPendingOrganizers).mockReset().mockResolvedValue([
      {
        id: 'org-pending-1',
        name: '待審核的主辦方',
        createdByMemberId: 'member-2',
        createdByDisplayName: '申請人',
        createdAtUtc: '2026-09-30T08:00:00Z',
      },
    ])
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
  })

  it('[BW-ADMIN-ENTRY-002] 非 Admin 使用者開啟選單後看得到其他項目，但看不到主辦方審核', async () => {
    const layout = await mountAtHomeAs('Member')

    await openMemberDropdown(layout)

    expect(findMenuItem('登出')).toBeDefined()
    expect(findMenuItem('我的主辦方')).toBeDefined()
    expect(findMenuItem('主辦方審核')).toBeUndefined()
  })

  // 使用真實 router（不 mock router.push），讓 beforeEach 守衛實際執行：證明入口可點、目標正確、
  // 且守衛對未切換 Organizer 的 Admin 放行。
  it('[BW-ADMIN-ENTRY-001] 未切換 Organizer 的 Admin 從選單點主辦方審核，導向審核頁並顯示內容', async () => {
    const layout = await mountAtHomeAs('Admin')

    await openMemberDropdown(layout)
    findMenuItem('主辦方審核')!.click()
    await flushPromises()

    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/admin/organizers'))
    await flushPromises()
    expect(layout.text()).toContain('待審核的主辦方')
  })
})
