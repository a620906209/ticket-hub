import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
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

// 頁面改為延遲載入後，第一次導航到某頁要先轉換並載入該頁（含 Element Plus），整套測試並行時可能超過預設 5 秒
describe('AdminLayout', { timeout: 30_000 }, () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([])
    const authStore = useAuthStore()
    // 場館頁要求已切換 Organizer（AWU-GUARD-002）；沒帶 OrganizerId claim 會被導到「我的主辦方」頁，
    // 讓測試實際渲染的是那一頁而不是場館頁。
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    await router.push('/admin/venues')
  })

  // 對應 AC: ADMIN-REDEEM-NAV-ENTRY（8.7.13）：非 Admin 的 Organizer 成員點擊選單後，真實 router 的 beforeEach 守衛
  // 放行並停在核銷頁——同時證明「可點選」「目標路徑正確」「守衛放行非 Admin 成員」。
  it('[ADMIN-REDEEM-NAV-ENTRY] 非 Admin 的 Organizer 成員點擊「票券核銷」選單後導覽至 /admin/redeem', async () => {
    const authStore = useAuthStore()
    authStore.member = { id: '2', email: 'member@example.com', displayName: 'Member', role: 'Member', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    const wrapper = mountLayout()

    const menuItem = wrapper.findAll('.el-menu-item').find((item) => item.text() === '票券核銷')
    expect(menuItem).toBeDefined()

    await menuItem!.trigger('click')
    await flushPromises()

    // ElMenu 的 router 模式經由 router.push 非同步導覽（含 beforeEach 守衛），單次 flushPromises 在高負載下不保證已完成。
    await vi.waitFor(() => {
      expect(router.currentRoute.value.path).toBe('/admin/redeem')
      expect(router.currentRoute.value.name).toBe('admin-redeem')
    })
  })
})

// AWU-LIST-002：切換成功（OrganizerId claim 更新）後，導覽列依 claim 中的 GUID 比對出並顯示對應名稱。
describe('AdminLayout 導覽列目前 Organizer 名稱顯示', { timeout: 30_000 }, () => {
  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    const authStore = useAuthStore()
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
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

// 訂單、核銷頁面與場館、活動相同，只要求已切換 Organizer（AWU-GUARD-007），選單對所有人顯示；
// 審核頁仍要求 Admin 角色（AWU-GUARD-004），只對 Admin 顯示。
describe('AdminLayout 導覽選單依角色顯示', { timeout: 30_000 }, () => {
  const generalMenuLabels = ['場館管理', '活動管理', '訂單管理', '票券核銷']

  beforeEach(async () => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([])
    vi.mocked(organizersApi.getPendingOrganizers).mockReset()
    vi.mocked(organizersApi.getPendingOrganizers).mockResolvedValue([])
  })

  function readMenuLabels(wrapper: ReturnType<typeof mountLayout>): string[] {
    return wrapper.find('.admin-nav-menu').findAll('.el-menu-item').map((item) => item.text())
  }

  // 用完全相等而非 arrayContaining，同時證明沒有「主辦方審核」、也沒有一般選單被誤藏。
  it('[AWU-NAV-001] 已切換 Organizer 的非 Admin 成員看到場館、活動、訂單、核銷選單，看不到審核選單', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')
    authStore.member = { id: '2', email: 'member@example.com', displayName: 'Member', role: 'Member', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    await router.push('/admin/venues')

    const wrapper = mountLayout()
    await flushPromises()

    expect(readMenuLabels(wrapper)).toEqual(generalMenuLabels)
  })

  it('[AWU-NAV-002] 已切換 Organizer 的 Admin 看到全部選單，含審核選單', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = fakeAccessTokenWithOrganizerId('org-1')
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    await router.push('/admin/venues')

    const wrapper = mountLayout()
    await flushPromises()

    expect(readMenuLabels(wrapper)).toEqual([...generalMenuLabels, '主辦方審核'])
  })

  // 點選一般項目導向選擇主辦方頁由 AWU-GUARD-002（router/index.test.ts）驗證，這裡不重複。
  it('[AWU-NAV-004] 尚未切換 Organizer 的 Admin 在審核頁仍看得到一般選單與審核選單', async () => {
    const authStore = useAuthStore()
    authStore.accessToken = 'access-token'
    authStore.member = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
    await router.push('/admin/organizers')

    const wrapper = mountLayout()
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('admin-organizers')
    expect(readMenuLabels(wrapper)).toEqual([...generalMenuLabels, '主辦方審核'])
  })
})

// AWU-MOBILE-NAV-003～007：窄螢幕抽屜選單。jsdom 不套用 media query，選單按鈕與水平選單同時在 DOM 中，
// 抽屜 Teleport 到 body，因此抽屜內的元素一律在 document.body 的 .admin-nav-drawer 內查找（design.md 決策 3、5）。
describe('AdminLayout 窄螢幕抽屜選單', { timeout: 30_000 }, () => {
  const generalMenuLabels = ['場館管理', '活動管理', '訂單管理', '票券核銷']
  const adminMember = { id: '1', email: 'admin@example.com', displayName: 'Admin', role: 'Admin', isActive: true, hasRegisteredRealName: false, realName: null, nationalIdLast4Masked: null }
  const nonAdminMember = { ...adminMember, id: '2', email: 'member@example.com', displayName: 'Member', role: 'Member' }
  let wrapper: ReturnType<typeof mountLayout> | null = null

  beforeEach(() => {
    setActivePinia(createPinia())
    vi.mocked(organizersApi.getMyOrganizers).mockReset()
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([])
    vi.mocked(organizersApi.getPendingOrganizers).mockReset()
    vi.mocked(organizersApi.getPendingOrganizers).mockResolvedValue([])
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    document.body.innerHTML = ''
  })

  async function signInAndVisit(member: typeof adminMember, path: string, hasOrganizerContext = true): Promise<void> {
    const authStore = useAuthStore()
    authStore.accessToken = hasOrganizerContext ? fakeAccessTokenWithOrganizerId('org-1') : 'access-token'
    authStore.member = member
    await router.push(path)
  }

  // 使用真實 Transition（stubs.transition = false），抽屜關閉時才會走到 after-leave 觸發 @closed（tasks.md 2.3 做法 (a)）。
  // AdminLayout 是 /admin/* 路由的父元件，直接掛載時內部 RouterView 會再渲染一份 AdminLayout，導覽列與抽屜各出現兩份；
  // stub 掉 RouterView 只留一份，路由結果改由 router.currentRoute 判定。
  async function mountAttachedLayout(): Promise<void> {
    wrapper = mount(AdminLayout, {
      attachTo: document.body,
      global: { plugins: [ElementPlus, router], stubs: { transition: false, RouterView: true } },
    })
    await flushPromises()
  }

  function findMenuButton(): HTMLElement {
    const buttons = document.body.querySelectorAll<HTMLElement>('.admin-nav [aria-label="開啟後台選單"]')
    expect(buttons).toHaveLength(1)
    return buttons[0]
  }

  function findDrawer(): HTMLElement {
    const drawers = document.body.querySelectorAll<HTMLElement>('.admin-nav-drawer')
    expect(drawers).toHaveLength(1)
    return drawers[0]
  }

  // 抽屜關閉後以 v-show 隱藏仍留在 DOM，「關閉」以自身或祖先被 display: none 判定，不以元素不存在判定。
  function isDrawerVisible(): boolean {
    for (let el: HTMLElement | null = findDrawer(); el; el = el.parentElement) {
      if (el.style.display === 'none') return false
    }
    return true
  }

  function findDrawerMenuItems(): HTMLElement[] {
    return [...findDrawer().querySelectorAll<HTMLElement>('.admin-nav-drawer-menu .el-menu-item')]
  }

  function findDrawerLogoutButton(): HTMLElement {
    const logoutButtons = [...findDrawer().querySelectorAll<HTMLElement>('button')].filter((button) => button.textContent?.trim() === '登出')
    expect(logoutButtons).toHaveLength(1)
    return logoutButtons[0]
  }

  async function openDrawer(): Promise<void> {
    findMenuButton().click()
    await flushPromises()
    await vi.waitFor(() => expect(isDrawerVisible()).toBe(true))
  }

  async function selectDrawerMenuItem(label: string): Promise<void> {
    const items = findDrawerMenuItems().filter((item) => item.textContent?.trim() === label)
    expect(items).toHaveLength(1)
    items[0].click()
    await flushPromises()
  }

  async function expectDrawerClosed(): Promise<void> {
    await vi.waitFor(() => {
      expect(isDrawerVisible()).toBe(false)
      expect(findMenuButton().getAttribute('aria-expanded')).toBe('false')
    })
  }

  // 完全相等比對：同時證明兩個選單共用同一份依角色產生的清單（design.md 決策 2），抽屜沒有多出或漏掉項目。
  it.each([
    { role: '非 Admin', member: nonAdminMember, expectedLabels: generalMenuLabels },
    { role: 'Admin', member: adminMember, expectedLabels: [...generalMenuLabels, '主辦方審核'] },
  ])('[AWU-MOBILE-NAV-003] $role 點選單按鈕後開啟「後台選單」抽屜，依角色顯示選單項目與登出', async ({ member, expectedLabels }) => {
    await signInAndVisit(member, '/admin/venues')
    await mountAttachedLayout()

    await openDrawer()

    expect(findDrawer().querySelector('.el-drawer__title')?.textContent?.trim()).toBe('後台選單')
    expect(findMenuButton().getAttribute('aria-expanded')).toBe('true')
    expect(findDrawerMenuItems().map((item) => item.textContent?.trim())).toEqual(expectedLabels)
    findDrawerLogoutButton()
  })

  it('[AWU-MOBILE-NAV-004] 非 Admin 在抽屜選取「票券核銷」後導覽至核銷頁並關閉抽屜', async () => {
    await signInAndVisit(nonAdminMember, '/admin/venues')
    await mountAttachedLayout()
    await openDrawer()

    await selectDrawerMenuItem('票券核銷')

    await vi.waitFor(() => {
      expect(router.currentRoute.value.path).toBe('/admin/redeem')
      expect(router.currentRoute.value.name).toBe('admin-redeem')
    })
    await expectDrawerClosed()
  })

  // 選取目前所在頁面時路由不會變更，以「選取」而非「路由變更」作為關閉時機才能涵蓋（design.md 決策 3）。
  it('[AWU-MOBILE-NAV-004] 在抽屜選取目前所在頁面的項目時仍關閉抽屜', async () => {
    await signInAndVisit(nonAdminMember, '/admin/venues')
    await mountAttachedLayout()
    await openDrawer()

    await selectDrawerMenuItem('場館管理')

    await expectDrawerClosed()
    expect(router.currentRoute.value.path).toBe('/admin/venues')
  })

  // 抽屜導覽仍經過路由守衛（AWU-GUARD-002），被導到其他頁面時抽屜同樣關閉。
  it('[AWU-MOBILE-NAV-004] 尚未切換 Organizer 的 Admin 在抽屜選取「場館管理」後被導向選擇主辦方頁並關閉抽屜', async () => {
    await signInAndVisit(adminMember, '/admin/organizers', false)
    await mountAttachedLayout()
    await openDrawer()

    await selectDrawerMenuItem('場館管理')

    await vi.waitFor(() => expect(router.currentRoute.value.name).toBe('my-organizers'))
    await expectDrawerClosed()
  })

  // 觸控與 Safari 點擊不會讓按鈕取得焦點，Element Plus 預設只把焦點還給開啟前的 activeElement（落到 body），
  // 因此不預先聚焦：只靠預設行為的實作在此會失敗（design.md 決策 4）。
  it('[AWU-MOBILE-NAV-005] 選單按鈕未取得焦點時點擊開啟抽屜，按 Esc 關閉後焦點回到選單按鈕', async () => {
    await signInAndVisit(nonAdminMember, '/admin/venues')
    await mountAttachedLayout()
    expect(document.activeElement).not.toBe(findMenuButton())
    await openDrawer()

    findDrawer().dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }))
    await flushPromises()

    await expectDrawerClosed()
    await vi.waitFor(() => expect(document.activeElement).toBe(findMenuButton()))
  })

  // authStore.logout 會等待後端登出 API；先 await 再關抽屜的話，等待期間遮罩與 focus trap 會一直蓋著畫面（design.md 決策 4）。
  it('[AWU-MOBILE-NAV-006] 在抽屜內點「登出」時，抽屜在登出完成前即關閉，完成後導向登入頁', async () => {
    await signInAndVisit(nonAdminMember, '/admin/venues')
    const authStore = useAuthStore()
    let resolveLogout!: () => void
    const logoutSpy = vi.spyOn(authStore, 'logout').mockReturnValue(new Promise<void>((resolve) => { resolveLogout = resolve }))
    await mountAttachedLayout()
    await openDrawer()

    findDrawerLogoutButton().click()
    await flushPromises()

    expect(findMenuButton().getAttribute('aria-expanded')).toBe('false')
    await vi.waitFor(() => expect(isDrawerVisible()).toBe(false))

    resolveLogout()
    await flushPromises()

    expect(logoutSpy).toHaveBeenCalledTimes(1)
    await vi.waitFor(() => expect(router.currentRoute.value.path).toBe('/login'))
  })

  it('[AWU-MOBILE-NAV-007] 主辦方名稱為 100 字元時，名稱入口的 title 屬性為完整名稱', async () => {
    const longName = '長'.repeat(100)
    vi.mocked(organizersApi.getMyOrganizers).mockResolvedValue([{ id: 'org-1', name: longName, status: 'Approved' }])
    await signInAndVisit(nonAdminMember, '/admin/venues')
    await mountAttachedLayout()

    const indicators = document.body.querySelectorAll('.organizer-indicator')
    expect(indicators).toHaveLength(1)
    expect(indicators[0].getAttribute('title')).toBe(longName)
  })
})
