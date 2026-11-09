import { authorizedRequest } from './httpClient'
import type { EventSeat, EventSummary, PagedResult, TicketType } from '../types/apiResponses'

export function getEvents(page = 1): Promise<PagedResult<EventSummary>> {
  return authorizedRequest(`/events?page=${page}`)
}

export function getEventSeats(eventId: string): Promise<EventSeat[]> {
  return authorizedRequest(`/events/${eventId}/seats`)
}

export function getTicketTypes(eventId: string): Promise<TicketType[]> {
  return authorizedRequest(`/events/${eventId}/ticket-types`)
}

export function getEventById(eventId: string): Promise<EventSummary> {
  return authorizedRequest(`/events/${eventId}`)
}
