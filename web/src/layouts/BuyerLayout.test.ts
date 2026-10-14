import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { RouterView } from 'vue-router'
import router from '../router/index'
import { useAuthStore } from '../stores/auth'
import * as organizersApi from '../api/organizers'
import * as eventsApi from '../api/events'
import * as membersApi from '../api/members'
import type { MemberProfile } from '../types/apiResponses'

vi.mock('../api/organizers')
// 買家端首頁（活動列表）會在 RouterView 內實際渲染並呼叫 API，mock 掉避免打出真實請求。
vi.mock('../api/events')
vi.mock('../api/members')

// decodeJwtPayload 只做 base64url decode + JSON.parse，不驗證簽章，見 utils/jwt.ts。
// 刻意不帶 OrganizerId claim：驗證入口不要求已切換 Organizer（BW-ADMIN-ENTRY-001）。
const ACCESS_TOKEN_WITHOUT_ORGANIZER = `header.${btoa(JSON.stringify({ sub: '1' }))}.signature`

let wrapper: VueWrapper | null = null

async function mountAtHomeAs(role: MemberProfile['role']): Promise<VueWrapper> {
  const authStore = useAuthStore()
  authStore.accessToken = ACCESS_TOKEN_WITHOUT_ORGANIZER
  authStore.member = { id: '1', email: 'user@example.com', displayName: '會員選單', role, isActive: true,
    hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
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

function findSwitcherPoppers(): HTMLElement[] {
  return Array.from(document.body.querySelectorAll<HTMLElement>('.area-switcher-popper'))
}

// 步驟比照 AreaSwitcher.test.ts 的 AWU-AREA-SWITCH-005：先證明開啟前隱藏，再點擊並等待 setTimeout 開啟
async function openAreaSwitcher(): Promise<HTMLElement> {
  expect(findSwitcherPoppers()).toHaveLength(1)
  expect(findSwitcherPoppers()[0].style.display).toBe('none')
  const triggers = document.body.querySelectorAll<HTMLElement>('.buyer-header .area-switcher .area-switcher-trigger')
  expect(triggers).toHaveLength(1)
  triggers[0].click()
  await vi.waitFor(() => {
    expect(findSwitcherPoppers()).toHaveLength(1)
    expect(findSwitcherPoppers()[0].style.display).not.toBe('none')
  })
  return findSwitcherPoppers()[0]
}

function readSwitcherLabels(popper: HTMLElement): string[] {
  return Array.from(popper.querySelectorAll('.el-dropdown-menu__item')).map((item) => item.textContent?.trim() ?? '')
}

// 頁面改為延遲載入後，第一次導航到某頁要先轉換並載入該頁（含 Element Plus），整套測試並行時可能超過預設 5 秒
describe('BuyerLayout 會員下拉選單與區域切換選單', { timeout: 30_000 }, () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(eventsApi.getEvents).mockReset().mockResolvedValue([])
    vi.mocked(membersApi.getMyProfile).mockReset().mockResolvedValue({
      id: '1', email: 'user@example.com', displayName: '會員選單', role: 'Member', isActive: true,
      hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null,
    })
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

  // 會員下拉選單已不放審核入口（改由區域切換選單），兩種角色都要驗，否則 Admin 專屬分支漏刪也不會被發現
  it.each(['Member', 'Admin'] as const)('[BW-ADMIN-ENTRY-003] %s 開啟會員下拉選單，有我的主辦方，沒有主辦方審核', async (role) => {
    const layout = await mountAtHomeAs(role)

    await openMemberDropdown(layout)

    expect(findMenuItem('我的主辦方')).toBeDefined()
    expect(findMenuItem('主辦方審核')).toBeUndefined()
  })

  // 完整清單比對：只斷言「找不到平台管理」時，選單沒渲染也會通過
  it('[BW-ADMIN-ENTRY-002][AWU-AREA-SWITCH-002] 非 Admin 在會員前台展開區域切換選單，恰為帳號、會員前台、主辦方後台三項', async () => {
    await mountAtHomeAs('Member')

    const popper = await openAreaSwitcher()

    expect(readSwitcherLabels(popper)).toEqual(['登入帳號：user@example.com', '會員前台（目前）', '主辦方後台'])
  })

  // 使用真實 router（不 mock router.push），讓 beforeEach 守衛實際執行：證明入口可點、/platform 的 redirect
  // 目標正確，且守衛對未切換 Organizer 的 Admin 放行。
  it('[BW-ADMIN-ENTRY-001] 未切換 Organizer 的 Admin 從區域切換選單點平台管理，導向審核頁並顯示內容', async () => {
    const layout = await mountAtHomeAs('Admin')

    const popper = await openAreaSwitcher()
    const items = [...popper.querySelectorAll<HTMLElement>('.el-dropdown-menu__item')].filter((item) => item.textContent?.trim() === '平台管理')
    expect(items).toHaveLength(1)
    items[0].click()
    await flushPromises()

    await vi.waitFor(() => {
      expect(router.currentRoute.value.path).toBe('/platform/organizers')
      expect(router.currentRoute.value.name).toBe('platform-organizers')
    })
    await flushPromises()
    expect(layout.text()).toContain('待審核的主辦方')
  })

  // 三個版面的切換鈕都在導覽列最右邊，切換區域時位置不跳動（design.md 決策 2）
  it('[AWU-AREA-SWITCH-007] 區域切換選單是頂列最後一個元素，我的訂單與會員選單在它左邊', async () => {
    await mountAtHomeAs('Member')

    const header = document.body.querySelector('.buyer-header')!
    expect(header.lastElementChild?.classList.contains('buyer-actions')).toBe(true)
    const actionChildren = [...header.lastElementChild!.children]
    expect(actionChildren.map((child) => child.classList.contains('area-switcher') ? 'switcher'
      : child.classList.contains('nav-link') ? child.textContent?.trim()
        : child.querySelector('.member-trigger') ? 'member' : child.className)).toEqual(['我的訂單', 'member', 'switcher'])
  })

  // 窄螢幕會員名稱以「…」截斷，title 是看到完整名稱的唯一途徑
  it('[AWU-AREA-NAV-OVERFLOW-002] 會員名稱觸發元素的 title 為完整名稱', async () => {
    await mountAtHomeAs('Member')
    const longName = '會'.repeat(15) + 'ABCDEFGHIJKLMNO'
    useAuthStore().member!.displayName = longName
    await flushPromises()

    const trigger = document.body.querySelector('.buyer-header .member-trigger')!
    expect(longName).toHaveLength(30)
    expect(trigger.textContent).toBe(longName)
    expect(trigger.getAttribute('title')).toBe(longName)
  })

  // 同時斷言「登入」連結存在：版面沒渲染時「沒有切換選單」也會成立
  it('[AWU-AREA-SWITCH-004] 未登入時會員前台不顯示區域切換選單', async () => {
    await router.push('/')
    wrapper = mount(RouterView, { global: { plugins: [ElementPlus, router] }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.findAll('.nav-link').some((link) => link.text() === '登入')).toBe(true)
    expect(document.body.querySelectorAll('.area-switcher')).toHaveLength(0)
  })

  it('[BW-RN-PAGE-009] 會員選單有實名資料，點選後導向實名資料頁', async () => {
    const layout = await mountAtHomeAs('Member')

    await openMemberDropdown(layout)
    findMenuItem('實名資料')!.click()
    await flushPromises()

    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/me/real-name'))
    await flushPromises()
    expect(membersApi.getMyProfile).toHaveBeenCalled()
    expect(layout.text()).toContain('登記後無法自行修改')
  })
})
