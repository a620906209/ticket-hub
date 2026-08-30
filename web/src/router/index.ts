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

import AdminLayout from '../layouts/AdminLayout.vue'
import AdminVenueListPage from '../pages/admin/VenueListPage.vue'
import AdminEventListPage from '../pages/admin/EventListPage.vue'
import AdminEventCreatePage from '../pages/admin/EventCreatePage.vue'
import AdminOrderListPage from '../pages/admin/AdminOrderListPage.vue'
import AdminOrderDetailPage from '../pages/admin/AdminOrderDetailPage.vue'
import AdminSalesReportPage from '../pages/admin/AdminSalesReportPage.vue'

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
      ],
    },
    {
      path: '/admin',
      component: AdminLayout,
      meta: { requiresAdmin: true },
      children: [
        { path: '', redirect: { name: 'admin-venues' } },
        { path: 'venues', name: 'admin-venues', component: AdminVenueListPage },
        { path: 'events', name: 'admin-events', component: AdminEventListPage },
        { path: 'events/new', name: 'admin-event-create', component: AdminEventCreatePage },
        { path: 'events/:eventId/sales-report', name: 'admin-sales-report', component: AdminSalesReportPage },
        { path: 'orders', name: 'admin-orders', component: AdminOrderListPage },
        { path: 'orders/:id', name: 'admin-order-detail', component: AdminOrderDetailPage },
      ],
    },
  ],
})

// 建立在「main.ts 的 bootstrapAsync() 已經跑完」的前提上，這裡只同步讀 store 狀態，
// 不自行 await 任何非同步流程（見設計文件決策 5）。
router.beforeEach((to) => {
  const authStore = useAuthStore()
  const requiresAdmin = to.matched.some((record) => record.meta.requiresAdmin)

  if (requiresAdmin) {
    if (!authStore.isAuthenticated) {
      return { name: 'login', query: { redirect: to.fullPath } }
    }
    if (!authStore.isAdmin) {
      return { name: 'events' }
    }
    return true
  }

  if (to.meta.requiresAuth && !authStore.isAuthenticated) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }

  return true
})

export default router
