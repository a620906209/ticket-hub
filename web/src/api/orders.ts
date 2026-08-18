import { authorizedRequest } from './httpClient'

export interface PlaceOrderSelection {
  eventSeatId: string
  ticketTypeId: string
}

export function placeOrder(selections: PlaceOrderSelection[]): Promise<{ id: string }> {
  return authorizedRequest('/orders', {
    method: 'POST',
    body: { selections },
  })
}

export function confirmOrder(orderId: string): Promise<void> {
  return authorizedRequest(`/orders/${orderId}/confirm`, { method: 'POST' })
}

export function cancelOrder(orderId: string): Promise<void> {
  return authorizedRequest(`/orders/${orderId}/cancel`, { method: 'POST' })
}
