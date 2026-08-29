import { authorizedRequest } from './httpClient'
import type { QueueStatus } from '../types/apiResponses'

export function joinQueue(eventId: string): Promise<{ id: string }> {
  return authorizedRequest(`/events/${eventId}/queue/entries`, { method: 'POST' })
}

export function getMyQueueStatus(eventId: string): Promise<QueueStatus> {
  return authorizedRequest(`/events/${eventId}/queue/entries/me`)
}
