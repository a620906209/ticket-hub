import { authorizedRequest, requestBlob } from './httpClient'
import type { MyOrderDetail, MyOrderSummary } from '../types/apiResponses'

export interface PlaceOrderSelection {
  eventSeatId: string | null
  ticketTypeId: string
  quantity?: number
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

export function getMyOrders(): Promise<MyOrderSummary[]> {
  return authorizedRequest('/orders')
}

export function getMyOrderDetail(orderId: string): Promise<MyOrderDetail> {
  return authorizedRequest(`/orders/${orderId}`)
}

export function getTicketQrCodeBlob(ticketId: string): Promise<Blob> {
  return requestBlob(`/tickets/${ticketId}/qr-code`)
}
