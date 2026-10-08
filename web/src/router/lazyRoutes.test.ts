import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { Router, RouteRecordRaw } from 'vue-router'

// vue-router 導航後會把解析好的元件寫回 route record，所以不與會導航的 index.test.ts 共用 router；
// 每個測試 vi.resetModules() 後重新 import，取得從未導航過的全新實例。
async function importFreshRouter(): Promise<Router> {
  vi.resetModules()
  return (await import('./index')).default
}

type ComponentLoader = () => Promise<{ default: unknown }>

async function resolveComponent(component: unknown): Promise<unknown> {
  return typeof component === 'function' ? (await (component as ComponentLoader)()).default : component
}

type RouteShape = { path: string; name: unknown; meta: unknown; redirect: unknown; children: RouteShape[] | undefined }

function toRouteShape(route: RouteRecordRaw): RouteShape {
  return {
    path: route.path,
    name: route.name,
    meta: route.meta,
    redirect: route.redirect,
    children: route.children?.map(toRouteShape),
  }
}

const child = (path: string, name: string | undefined, meta?: Record<string, boolean>, redirect?: unknown): RouteShape => ({
  path,
  name,
  meta,
  redirect,
  children: undefined,
})

// 依 bundle-splitting 開始前的 router/index.ts 逐項抄寫（BS-014 基準）
const EXPECTED_ROUTE_TREE: RouteShape[] = [
  {
    path: '/',
    name: undefined,
    meta: undefined,
    redirect: undefined,
    children: [
      child('', 'events'),
      child('login', 'login'),
      child('register', 'register'),
      child('events/:id', 'event-detail'),
      child('order-result/:id', 'order-result', { requiresAuth: true }),
      child('orders', 'my-orders', { requiresAuth: true }),
      child('orders/:id', 'order-detail', { requiresAuth: true }),
      child('me/real-name', 'real-name', { requiresAuth: true }),
      child('organizers/apply', 'organizer-apply', { requiresAuth: true }),
      child('organizers', 'my-organizers', { requiresAuth: true }),
    ],
  },
  {
    path: '/admin',
    name: undefined,
    meta: undefined,
    redirect: undefined,
    children: [
      child('', undefined, undefined, { name: 'admin-venues' }),
      child('venues', 'admin-venues', { requiresOrganizerContext: true }),
      child('events', 'admin-events', { requiresOrganizerContext: true }),
      child('events/new', 'admin-event-create', { requiresOrganizerContext: true }),
      child('events/:eventId/sales-report', 'admin-sales-report', { requiresOrganizerContext: true }),
      child('orders', 'admin-orders', { requiresOrganizerContext: true }),
      child('orders/:id', 'admin-order-detail', { requiresOrganizerContext: true }),
      child('redeem', 'admin-redeem', { requiresOrganizerContext: true }),
      child('organizers', 'admin-organizers', { requiresAdmin: true }),
    ],
  },
]

// route name（頂層 layout 以 path）→ 元件模組
const EXPECTED_COMPONENTS: Record<string, () => Promise<{ default: unknown }>> = {
  '/': () => import('../layouts/BuyerLayout.vue'),
  events: () => import('../pages/buyer/EventListPage.vue'),
  login: () => import('../pages/buyer/LoginPage.vue'),
  register: () => import('../pages/buyer/RegisterPage.vue'),
  'event-detail': () => import('../pages/buyer/EventDetailPage.vue'),
  'order-result': () => import('../pages/buyer/OrderResultPage.vue'),
  'my-orders': () => import('../pages/buyer/MyOrdersPage.vue'),
  'order-detail': () => import('../pages/buyer/OrderDetailPage.vue'),
  'real-name': () => import('../pages/buyer/RealNamePage.vue'),
  'organizer-apply': () => import('../pages/organizers/OrganizerApplyPage.vue'),
  'my-organizers': () => import('../pages/organizers/MyOrganizersPage.vue'),
  '/admin': () => import('../layouts/AdminLayout.vue'),
  'admin-venues': () => import('../pages/admin/VenueListPage.vue'),
  'admin-events': () => import('../pages/admin/EventListPage.vue'),
  'admin-event-create': () => import('../pages/admin/EventCreatePage.vue'),
  'admin-sales-report': () => import('../pages/admin/AdminSalesReportPage.vue'),
  'admin-orders': () => import('../pages/admin/AdminOrderListPage.vue'),
  'admin-order-detail': () => import('../pages/admin/AdminOrderDetailPage.vue'),
  'admin-redeem': () => import('../pages/admin/RedemptionScannerPage.vue'),
  'admin-organizers': () => import('../pages/admin/AdminOrganizersPage.vue'),
}

function flattenRoutes(routes: RouteRecordRaw[]): RouteRecordRaw[] {
  return routes.flatMap((route) => [route, ...flattenRoutes(route.children ?? [])])
}

describe('router 延遲載入與路由設定', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  // 首次 import 全部頁面（含 inline 轉換的 Element Plus）在容器內約需數秒，超過預設 5 秒
  it('[BS-005] 18 個頁面與 AdminLayout 為延遲載入，且都能載入成 Vue 元件', { timeout: 30_000 }, async () => {
    const router = await importFreshRouter()
    const lazyRecords = router.getRoutes().filter((record) => typeof record.components?.default === 'function')

    expect(lazyRecords).toHaveLength(19)
    for (const record of lazyRecords) {
      const component = await resolveComponent(record.components!.default)
      expect(typeof component, `${record.path} 應解析成 Vue 元件物件`).toBe('object')
      expect(
        'setup' in (component as object) || 'render' in (component as object),
        `${record.path} 的元件應含 setup 或 render`,
      ).toBe(true)
    }
  })

  it('[BS-014] path、name、meta、redirect 與巢狀結構和元件對應不變', { timeout: 30_000 }, async () => {
    const router = await importFreshRouter()

    expect(router.options.routes.map(toRouteShape)).toStrictEqual(EXPECTED_ROUTE_TREE)

    const routesWithComponent = flattenRoutes([...router.options.routes]).filter((route) => 'component' in route && route.component)
    expect(routesWithComponent).toHaveLength(Object.keys(EXPECTED_COMPONENTS).length)
    for (const route of routesWithComponent) {
      const key = String(route.name ?? route.path)
      const loadExpected = EXPECTED_COMPONENTS[key]
      expect(loadExpected, `對照表缺少 ${key}`).toBeDefined()
      const actual = await resolveComponent((route as { component: unknown }).component)
      expect(actual, `${key} 的元件`).toBe((await loadExpected()).default)
    }
  })
})
