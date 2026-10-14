import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import PlatformLayout from './PlatformLayout.vue'
import router from '../router/index'
import { useAuthStore } from '../stores/auth'
import * as organizersApi from '../api/organizers'

vi.mock('../api/organizers')
// 會員前台首頁與「我的主辦方」頁可能在導覽後被載入，mock 掉避免打出真實請求
vi.mock('../api/events')

let wrapper: VueWrapper | null = null

// 刻意不帶 OrganizerId claim：平台管理不要求已切換 Organizer，且 AWU-AREA-SWITCH-003 要驗證未切換時的導向
async function signInAsAdminAndVisitReviewPage(): Promise<void> {
  const authStore = useAuthStore()
  authStore.accessToken = 'access-token'
  authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true,
    hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
  await router.push('/platform/organizers')
}

// PlatformLayout 是 /platform/* 的父元件，直接掛載時內部 RouterView 會再渲染一份版面；stub 掉只留一份，
// 路由結果改由 router.currentRoute 判定（比照 AdminLayout.test.ts）
async function mountLayout(): Promise<void> {
  wrapper = mount(PlatformLayout, {
    attachTo: document.body,
    global: { plugins: [ElementPlus, router], stubs: { RouterView: true } },
  })
  await flushPromises()
}

function findSwitcherPoppers(): HTMLElement[] {
  return [...document.body.querySelectorAll<HTMLElement>('.area-switcher-popper')]
}

// 步驟比照 AreaSwitcher.test.ts 的 AWU-AREA-SWITCH-005：先證明開啟前隱藏，再點擊並等待 setTimeout 開啟
async function openAreaSwitcher(): Promise<HTMLElement> {
  expect(findSwitcherPoppers()).toHaveLength(1)
  expect(findSwitcherPoppers()[0].style.display).toBe('none')
  wrapper!.find('.platform-nav .area-switcher-trigger').element.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => {
    expect(findSwitcherPoppers()).toHaveLength(1)
    expect(findSwitcherPoppers()[0].style.display).not.toBe('none')
  })
  return findSwitcherPoppers()[0]
}

async function selectSwitcherItem(label: string): Promise<void> {
  const popper = await openAreaSwitcher()
  const items = [...popper.querySelectorAll<HTMLElement>('.el-dropdown-menu__item')].filter((item) => item.textContent?.trim() === label)
  expect(items).toHaveLength(1)
  items[0].click()
  await flushPromises()
}

describe('PlatformLayout 平台管理版面', { timeout: 30_000 }, () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset().mockResolvedValue([])
    vi.mocked(organizersApi.getPendingOrganizers).mockReset().mockResolvedValue([])
  })

  // Teleport 的 popper 掛在 document.body，不 unmount 會殘留並讓下一支測試的命中數變成 2
  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    vi.restoreAllMocks()
  })

  it('[AWU-PLATFORM-NAV-001] 導覽列有區域切換選單（平台管理）、恰一項主辦方審核選單與登出', async () => {
    await signInAsAdminAndVisitReviewPage()
    await mountLayout()

    const switchers = wrapper!.findAll('.platform-nav .area-switcher')
    expect(switchers).toHaveLength(1)
    expect(switchers[0].find('.area-switcher-trigger').text()).toContain('平台管理')
    expect(wrapper!.find('.platform-nav-menu').findAll('.el-menu-item').map((item) => item.text())).toEqual(['主辦方審核'])
    expect(wrapper!.findAll('.platform-nav-logout')).toHaveLength(1)
    // 沒有抽屜（決策 4）：只有一個選單項目，窄螢幕也放得下
    expect(document.body.querySelectorAll('.el-drawer')).toHaveLength(0)
  })

  // 三個版面的切換鈕都在導覽列最右邊，切換區域時位置不跳動（design.md 決策 2）
  it('[AWU-AREA-SWITCH-007] 區域切換選單是導覽列最後一個元素，登出在它左邊', async () => {
    await signInAsAdminAndVisitReviewPage()
    await mountLayout()

    const navChildren = [...wrapper!.find('.platform-nav').element.children]
    expect(navChildren.map((child) => child.classList.contains('platform-nav-menu') ? 'menu'
      : child.classList.contains('area-switcher') ? 'switcher'
        : child.classList.contains('platform-nav-logout') ? 'logout' : child.className)).toEqual(['menu', 'logout', 'switcher'])
  })

  it('[AWU-PLATFORM-NAV-001] 主辦方審核選單項目前往 /platform/organizers', async () => {
    await signInAsAdminAndVisitReviewPage()
    await router.push('/')
    await mountLayout()

    await wrapper!.find('.platform-nav-menu .el-menu-item').trigger('click')

    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/platform/organizers'))
  })

  it('[AWU-PLATFORM-NAV-001] 點選登出後執行登出並導向 /login', async () => {
    await signInAsAdminAndVisitReviewPage()
    const logoutSpy = vi.spyOn(useAuthStore(), 'logout').mockResolvedValue()
    await mountLayout()

    await wrapper!.find('.platform-nav-logout').trigger('click')
    await flushPromises()

    expect(logoutSpy).toHaveBeenCalledTimes(1)
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/login'))
  })

  // 使用真實 router，主辦方後台的守衛實際執行：未切換 Organizer 時應被導到「選擇主辦方」（AWU-GUARD-002）
  it('[AWU-AREA-SWITCH-003] 未切換 Organizer 的 Admin 在平台管理點主辦方後台，被導向選擇主辦方頁面', async () => {
    await signInAsAdminAndVisitReviewPage()
    await mountLayout()

    await selectSwitcherItem('主辦方後台')

    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('my-organizers'))
  })

  it('[AWU-AREA-SWITCH-003] 在平台管理點會員前台，路徑為 /', async () => {
    await signInAsAdminAndVisitReviewPage()
    await mountLayout()

    await selectSwitcherItem('會員前台')

    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/'))
  })
})
