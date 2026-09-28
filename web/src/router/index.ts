import { createRouter, createWebHistory } from 'vue-router'
import { useAuthStore } from '../stores/auth'

import BuyerLayout from '../layouts/BuyerLayout.vue'
import LoginPage from '../pages/buyer/LoginPage.vue'
import RegisterPage from '../pages/buyer/RegisterPage.vue'
import EventListPage from '../pages/buyer/EventListPage.vue'
import EventDetailPage from '../pages/buyer/EventDetailPage.vue'
import OrderResultPage from '../pages/buyer/OrderResultPage.vue'
import MyOrdersPage from '../pages/buyer/MyOrdersPage.vue'
import OrderDetailPage from '../pages/buyer/OrderDetailPage.vue'
import OrganizerApplyPage from '../pages/organizers/OrganizerApplyPage.vue'
import MyOrganizersPage from '../pages/organizers/MyOrganizersPage.vue'

import AdminLayout from '../layouts/AdminLayout.vue'
import AdminVenueListPage from '../pages/admin/VenueListPage.vue'
import AdminEventListPage from '../pages/admin/EventListPage.vue'
import AdminEventCreatePage from '../pages/admin/EventCreatePage.vue'
import AdminOrderListPage from '../pages/admin/AdminOrderListPage.vue'
import AdminOrderDetailPage from '../pages/admin/AdminOrderDetailPage.vue'
import AdminSalesReportPage from '../pages/admin/AdminSalesReportPage.vue'
import RedemptionScannerPage from '../pages/admin/RedemptionScannerPage.vue'
import AdminOrganizersPage from '../pages/admin/AdminOrganizersPage.vue'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      component: BuyerLayout,
      children: [
        { path: '', name: 'events', component: EventListPage },
        { path: 'login', name: 'login', component: LoginPage },
        { path: 'register', name: 'register', component: RegisterPage },
        { path: 'events/:id', name: 'event-detail', component: EventDetailPage },
        {
          path: 'order-result/:id',
          name: 'order-result',
          component: OrderResultPage,
          meta: { requiresAuth: true },
        },
        { path: 'orders', name: 'my-orders', component: MyOrdersPage, meta: { requiresAuth: true } },
        { path: 'orders/:id', name: 'order-detail', component: OrderDetailPage, meta: { requiresAuth: true } },
        // 已登入任何角色的會員皆可申請／查看自己所屬的主辦方，本次不修改既有後台路由守衛規則
        // （不歸在 /admin 之下，見 organizer-management proposal.md「Capabilities」admin-web-ui 段落）。
        { path: 'organizers/apply', name: 'organizer-apply', component: OrganizerApplyPage, meta: { requiresAuth: true } },
        { path: 'organizers', name: 'my-organizers', component: MyOrganizersPage, meta: { requiresAuth: true } },
      ],
    },
    {
      path: '/admin',
      component: AdminLayout,
      children: [
        { path: '', redirect: { name: 'admin-venues' } },
        // 活動、場館頁面：event-management-organizer-scoping 起改為要求已切換至一個 Organizer
        // （AWU-GUARD-002／003），不再是「角色為 Admin」。
        { path: 'venues', name: 'admin-venues', component: AdminVenueListPage, meta: { requiresOrganizerContext: true } },
        { path: 'events', name: 'admin-events', component: AdminEventListPage, meta: { requiresOrganizerContext: true } },
        { path: 'events/new', name: 'admin-event-create', component: AdminEventCreatePage, meta: { requiresOrganizerContext: true } },
        // 銷售報表、訂單、核銷後端已改為 RequireOrganizerContext，比照活動、場館頁面（AWU-GUARD-007，
        // 見 order-report-redemption-organizer-scoping design.md Decision 3）。
        { path: 'events/:eventId/sales-report', name: 'admin-sales-report', component: AdminSalesReportPage, meta: { requiresOrganizerContext: true } },
        { path: 'orders', name: 'admin-orders', component: AdminOrderListPage, meta: { requiresOrganizerContext: true } },
        { path: 'orders/:id', name: 'admin-order-detail', component: AdminOrderDetailPage, meta: { requiresOrganizerContext: true } },
        { path: 'redeem', name: 'admin-redeem', component: RedemptionScannerPage, meta: { requiresOrganizerContext: true } },
        // 審核頁面只需要 Admin 角色，不需要已切換 Organizer（AWU-GUARD-004／005）。
        { path: 'organizers', name: 'admin-organizers', component: AdminOrganizersPage, meta: { requiresAdmin: true } },
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
