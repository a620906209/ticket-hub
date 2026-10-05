import { authorizedRequest } from './httpClient'
import type {
  AdminEventSummary,
  OrderDetail,
  OrderSummary,
  SalesReport,
  SeatMapDetail,
  TicketHolder,
  VenueDetail,
  VenueSummary,
} from '../types/apiResponses'

export interface SeatInput {
  zoneCode: string
  seatNumber: string
}

export function createVenue(name: string): Promise<{ id: string }> {
  return authorizedRequest('/admin/venues', { method: 'POST', body: { name } })
}

export function createSeatMap(venueId: string, seats: SeatInput[]): Promise<{ id: string }> {
  return authorizedRequest(`/admin/venues/${venueId}/seat-maps`, { method: 'POST', body: { seats } })
}

export interface CreateEventInput {
  title: string
  startAtUtc: string
  venueId: string
  seatMapId: string
  description?: string
  posterUrl?: string
  maxTicketsPerOrder?: number
  isRealNameRequired?: boolean
  salesStartAtUtc?: string
  salesEndAtUtc?: string
}

// 參數超過 3 個改收物件（event-sales-window-web-ui design.md 決策 6）；
// 未提供的選填欄位為 undefined，JSON.stringify 會略過，等同不放進 body。
export function createEvent(input: CreateEventInput): Promise<{ id: string }> {
  return authorizedRequest('/admin/events', {
    method: 'POST',
    body: { ...input, isRealNameRequired: input.isRealNameRequired ?? false },
  })
}

export function createTicketType(
  eventId: string,
  zoneCode: string,
  price: number,
  requiresSeat = true,
  availableQuantity?: number,
): Promise<{ id: string }> {
  return authorizedRequest(`/admin/events/${eventId}/ticket-types`, {
    method: 'POST',
    body: requiresSeat ? { zoneCode, price, requiresSeat } : { zoneCode, price, requiresSeat, availableQuantity },
  })
}

export function getAdminOrders(): Promise<OrderSummary[]> {
  return authorizedRequest('/admin/orders')
}

export function getAdminOrderById(orderId: string): Promise<OrderDetail> {
  return authorizedRequest(`/admin/orders/${orderId}`)
}

export function getVenues(): Promise<VenueSummary[]> {
  return authorizedRequest('/admin/venues')
}

export function getVenueById(venueId: string): Promise<VenueDetail> {
  return authorizedRequest(`/admin/venues/${venueId}`)
}

export function getSeatMapById(venueId: string, seatMapId: string): Promise<SeatMapDetail> {
  return authorizedRequest(`/admin/venues/${venueId}/seat-maps/${seatMapId}`)
}

export function getAdminEvents(): Promise<AdminEventSummary[]> {
  return authorizedRequest('/admin/events')
}

export function getEventSalesReport(eventId: string): Promise<SalesReport> {
  return authorizedRequest(`/admin/events/${eventId}/sales-report`)
}

// isHolderVerified 只在操作人員確認比對證件後才送出；未確認時不帶此欄位，由後端視為 false
// （real-name-verification design.md 決策 4）。
export function redeemTicket(ticketId: string, signature: string | null, isHolderVerified = false): Promise<void> {
  return authorizedRequest(`/admin/tickets/${ticketId}/redeem`, {
    method: 'PATCH',
    body: isHolderVerified ? { signature, isHolderVerified } : { signature },
  })
}

export function getTicketHolder(ticketId: string): Promise<TicketHolder> {
  return authorizedRequest(`/admin/tickets/${ticketId}/holder`)
}
