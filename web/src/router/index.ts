import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'

// 頁面一律延遲載入，買家首屏才不會下載後台頁面與 jsqr（bundle-splitting）；BuyerLayout 是首頁外框，
// 維持靜態 import 以省一次 chunk 請求
import BuyerLayout from '../layouts/BuyerLayout.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      component: BuyerLayout,
      children: [
        { path: '', name: 'events', component: () => import('../pages/buyer/EventListPage.vue') },
        { path: 'login', name: 'login', component: () => import('../pages/buyer/LoginPage.vue') },
        { path: 'register', name: 'register', component: () => import('../pages/buyer/RegisterPage.vue') },
        { path: 'events/:id', name: 'event-detail', component: () => import('../pages/buyer/EventDetailPage.vue') },
        {
          path: 'order-result/:id',
          name: 'order-result',
          component: () => import('../pages/buyer/OrderResultPage.vue'),
          meta: { requiresAuth: true },
        },
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
        // 審核頁面只需要 Admin 角色，不需要已切換 Organizer（AWU-GUARD-004／005）。
        { path: 'organizers', name: 'admin-organizers', component: () => import('../pages/admin/AdminOrganizersPage.vue'), meta: { requiresAdmin: true } },
      ],
    },
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
