import { authorizedRequest } from './httpClient'
import type { OrganizerSummary, PendingOrganizer, SwitchOrganizerContextResult } from '../types/apiResponses'

export function applyForOrganizer(name: string): Promise<{ id: string }> {
  return authorizedRequest('/organizers', { method: 'POST', body: { name } })
}

export function getMyOrganizers(): Promise<OrganizerSummary[]> {
  return authorizedRequest('/organizers/mine')
}

export function switchOrganizerContext(organizerId: string, refreshToken: string): Promise<SwitchOrganizerContextResult> {
  return authorizedRequest(`/organizers/${organizerId}/switch-context`, {
    method: 'POST',
    body: { refreshToken },
  })
}

export function getPendingOrganizers(): Promise<PendingOrganizer[]> {
  return authorizedRequest('/admin/organizers?status=Pending')
}

export function approveOrganizer(organizerId: string): Promise<void> {
  return authorizedRequest(`/admin/organizers/${organizerId}/approve`, { method: 'PATCH' })
}

export function rejectOrganizer(organizerId: string): Promise<void> {
  return authorizedRequest(`/admin/organizers/${organizerId}/reject`, { method: 'PATCH' })
}
