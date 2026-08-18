import { authorizedRequest } from './httpClient'
import type { EventSeat, EventSummary, TicketType } from '../types/apiResponses'

export function getEvents(): Promise<EventSummary[]> {
  return authorizedRequest('/events')
}

export function getEventSeats(eventId: string): Promise<EventSeat[]> {
  return authorizedRequest(`/events/${eventId}/seats`)
}

export function getTicketTypes(eventId: string): Promise<TicketType[]> {
  return authorizedRequest(`/events/${eventId}/ticket-types`)
}
