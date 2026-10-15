import { createRouter, createWebHistory, type RouteLocationNormalized } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { cancelPendingScrollRestoration, waitForRestorableScrollPosition } from './scrollRestoration'

// 頁面一律延遲載入，買家首屏才不會下載後台頁面與 jsqr（bundle-splitting）；BuyerLayout 是首頁外框，
// 維持靜態 import 以省一次 chunk 請求
import BuyerLayout from '../layouts/BuyerLayout.vue'

function isOnlyStepQueryChanged(to: RouteLocationNormalized, from: RouteLocationNormalized): boolean {
  if (to.path !== from.path || to.hash !== from.hash || to.query.step === from.query.step) return false
  // 逐 key 比對，query 物件的 key 順序不同不影響結果
  const otherKeys = new Set([...Object.keys(to.query), ...Object.keys(from.query)])
  otherKeys.delete('step')
  return [...otherKeys].every((key) => JSON.stringify(to.query[key]) === JSON.stringify(from.query[key]))
}

const router = createRouter({
  history: createWebHistory(),
  // 沒設定時 SPA 換頁會沿用上一頁的捲動位置，從長列表點進詳情頁會停在頁面中段。上一頁／下一頁交回 savedPosition，
  // 但資料非同步載入的頁面在導覽完成當下高度不足，所以等高度足夠（最多 3 秒）才還原（frontend-minor-polish design.md 決策 3）
  // 只切換確認步驟（step query）時不捲動、也不還原 savedPosition，捲動交給元件移焦點處理，避免兩者競爭
  // （order-confirmation-page design.md 決策 6）
  scrollBehavior: (to, from, savedPosition) => {
    // 每次導覽完成都先放棄上一次等待中的還原，不靠 vue-router 的 currentRoute 檢查擋下舊位置
    cancelPendingScrollRestoration()
    if (isOnlyStepQueryChanged(to, from)) return false
    return savedPosition ? waitForRestorableScrollPosition(savedPosition) : { top: 0 }
  },
  routes: [
    {
      path: '/',
      component: BuyerLayout,
      children: [
        { path: '', name: 'events', component: () => import('../pages/buyer/EventListPage.vue') },
        { path: 'login', name: 'login', component: () => import('../pages/buyer/LoginPage.vue') },
        { path: 'register', name: 'register', component: () => import('../pages/buyer/RegisterPage.vue') },
        { path: 'events/:id', name: 'event-detail', component: () => import('../pages/buyer/EventDetailPage.vue') },
        // 結果頁已移除（order-confirmation-page design.md 決策 9），舊網址與書籤導向同一筆訂單的明細頁；
        // 登入要求、權限與找不到訂單由 order-detail 的守衛與頁面處理
        { path: 'order-result/:id', redirect: (to) => ({ name: 'order-detail', params: { id: to.params.id } }) },
        { path: 'orders', name: 'my-orders', component: () => import('../pages/buyer/MyOrdersPage.vue'), meta: { requiresAuth: true } },
        { path: 'orders/:id', name: 'order-detail', component: () => import('../pages/buyer/OrderDetailPage.vue'), meta: { requiresAuth: true } },
        { path: 'me/real-name', name: 'real-name', component: () => import('../pages/buyer/RealNamePage.vue'), meta: { requiresAuth: true } },
        // 已登入任何角色的會員皆可申請／查看自己所屬的主辦方，本次不修改既有後台路由守衛規則
        // （不歸在 /admin 之下，見 organizer-management proposal.md「Capabilities」admin-web-ui 段落）。
        { path: 'organizers/apply', name: 'organizer-apply', component: () => import('../pages/organizers/OrganizerApplyPage.vue'), meta: { requiresAuth: true } },
        { path: 'organizers', name: 'my-organizers', component: () => import('../pages/organizers/MyOrganizersPage.vue'), meta: { requiresAuth: true } },
      ],
    },
    {
      path: '/admin',
      component: () => import('../layouts/AdminLayout.vue'),
      children: [
        { path: '', redirect: { name: 'admin-venues' } },
        // 活動、場館頁面：event-management-organizer-scoping 起改為要求已切換至一個 Organizer
        // （AWU-GUARD-002／003），不再是「角色為 Admin」。
        { path: 'venues', name: 'admin-venues', component: () => import('../pages/admin/VenueListPage.vue'), meta: { requiresOrganizerContext: true } },
        { path: 'events', name: 'admin-events', component: () => import('../pages/admin/EventListPage.vue'), meta: { requiresOrganizerContext: true } },
        { path: 'events/new', name: 'admin-event-create', component: () => import('../pages/admin/EventCreatePage.vue'), meta: { requiresOrganizerContext: true } },
        // 銷售報表、訂單、核銷後端已改為 RequireOrganizerContext，比照活動、場館頁面（AWU-GUARD-007，
        // 見 order-report-redemption-organizer-scoping design.md Decision 3）。
        { path: 'events/:eventId/sales-report', name: 'admin-sales-report', component: () => import('../pages/admin/AdminSalesReportPage.vue'), meta: { requiresOrganizerContext: true } },
        { path: 'orders', name: 'admin-orders', component: () => import('../pages/admin/AdminOrderListPage.vue'), meta: { requiresOrganizerContext: true } },
        { path: 'orders/:id', name: 'admin-order-detail', component: () => import('../pages/admin/AdminOrderDetailPage.vue'), meta: { requiresOrganizerContext: true } },
        { path: 'redeem', name: 'admin-redeem', component: () => import('../pages/admin/RedemptionScannerPage.vue'), meta: { requiresOrganizerContext: true } },
        // 審核頁搬到 /platform 後保留舊網址與書籤；redirect 後的目的路由仍經過守衛（AWU-GUARD-004／008）
        { path: 'organizers', redirect: { name: 'platform-organizers' } },
      ],
    },
    {
      path: '/platform',
      component: () => import('../layouts/PlatformLayout.vue'),
      children: [
        { path: '', redirect: { name: 'platform-organizers' } },
        // 審核頁面只需要 Admin 角色，不需要已切換 Organizer（AWU-GUARD-004／005）。
        { path: 'organizers', name: 'platform-organizers', component: () => import('../pages/admin/AdminOrganizersPage.vue'), meta: { requiresAdmin: true } },
      ],
    },
    // 未知網址（打錯或舊連結）沒有對應路由時整頁空白，導回活動列表
    { path: '/:pathMatch(.*)*', redirect: { name: 'events' } },
  ],
})

// 建立在「main.ts 的 bootstrapAsync() 已經跑完」的前提上，這裡只同步讀 store 狀態，
// 不自行 await 任何非同步流程（見設計文件決策 5）。
router.beforeEach((to) => {
  const authStore = useAuthStore()
  const requiresAdmin = to.matched.some((record) => record.meta.requiresAdmin)
  const requiresOrganizerContext = to.matched.some((record) => record.meta.requiresOrganizerContext)

  if (requiresAdmin || requiresOrganizerContext) {
    if (!authStore.isAuthenticated) {
      return { name: 'login', query: { redirect: to.fullPath } }
    }
    if (requiresAdmin && !authStore.isAdmin) {
      return { name: 'events' }
    }
    if (requiresOrganizerContext && !authStore.organizerId) {
      return { name: 'my-organizers' }
    }
    return true
  }

  if (to.meta.requiresAuth && !authStore.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  return true
})

export default router
