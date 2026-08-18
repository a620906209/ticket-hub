import { authorizedRequest } from './httpClient'
import type {
  AdminEventSummary,
  OrderDetail,
  OrderSummary,
  SeatMapDetail,
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

export function createEvent(
  title: string,
  startAtUtc: string,
  venueId: string,
  seatMapId: string,
  description?: string,
  posterUrl?: string,
  maxTicketsPerOrder?: number,
): Promise<{ id: string }> {
  return authorizedRequest('/admin/events', {
    method: 'POST',
    body: { title, startAtUtc, venueId, seatMapId, description, posterUrl, maxTicketsPerOrder },
  })
}

export function createTicketType(eventId: string, zoneCode: string, price: number): Promise<{ id: string }> {
  return authorizedRequest(`/admin/events/${eventId}/ticket-types`, {
    method: 'POST',
    body: { zoneCode, price },
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
