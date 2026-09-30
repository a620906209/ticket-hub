import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { createMemoryHistory, createRouter, RouterView, type Router } from 'vue-router'
import type { Component } from 'vue'
import type { MyOrderDetail } from '../../types/apiResponses'

// OrderResultPage、OrderDetailPage 與 usePendingOrderActions 的測試共用：
// 以真實記憶體路由掛載，才能驗證「同一元件實例切換 :id」時的競態處理（design.md 決策 2）。

export function createDeferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

export const HELD_UNTIL_UTC = '2026-12-31T12:00:00Z'

export function buildPendingOrder(overrides: Partial<MyOrderDetail> = {}): MyOrderDetail {
  return {
    id: 'order-1',
    eventId: 'event-1',
    status: 'Pending',
    heldUntilUtc: HELD_UNTIL_UTC,
    items: [{ id: 'item-1', eventSeatId: 'seat-1', ticketTypeId: null, quantity: 1, unitPrice: 1200, tickets: [] }],
    ...overrides,
  }
}

export function buildPaidOrder(overrides: Partial<MyOrderDetail> = {}): MyOrderDetail {
  return buildPendingOrder({
    status: 'Paid',
    items: [
      {
        id: 'item-1',
        eventSeatId: 'seat-1',
        ticketTypeId: null,
        quantity: 1,
        unitPrice: 1200,
        tickets: [{ id: 'ticket-1', status: 'Issued' }],
      },
    ],
    ...overrides,
  })
}

export async function mountOrderPageAt(
  routePath: string,
  pagePath: string,
  page: Component,
): Promise<{ wrapper: VueWrapper; router: Router }> {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: pagePath, name: 'order-page', component: page },
      { path: '/orders', name: 'my-orders', component: { render: () => null } },
    ],
  })
  await router.push(routePath)
  await router.isReady()
  const wrapper = mount(RouterView, { global: { plugins: [ElementPlus, router] } })
  await flushPromises()
  return { wrapper, router }
}

export function findButton(wrapper: VueWrapper, text: string) {
  return wrapper.findAll('button').find((button) => button.text() === text)
}

export function isButtonDisabled(wrapper: VueWrapper, text: string): boolean {
  return findButton(wrapper, text)?.attributes('disabled') !== undefined
}
