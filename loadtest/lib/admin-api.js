import { fail } from 'k6';
import {
  COUNT_TICKET_ZONE,
  HOT_ZONE,
  OTHER_ZONE_COUNT,
  TICKET_LIMIT,
  TICKET_PRICE,
  TOTAL_SEATS,
} from './config.js';
import { authHeaders, describeFailure, get, parseJson, postJson, withTags } from './http.js';

const SALES_START_OFFSET_MS = 60_000;
const EVENT_START_OFFSET_MS = 24 * 60 * 60 * 1000;

/** 19 個非目標分區 Z01–Z19，平均分配 1950 席，餘數放最後一區（design 決策 5）。 */
export function buildOtherZones() {
  const otherSeats = TOTAL_SEATS - TICKET_LIMIT;
  const baseSize = Math.floor(otherSeats / OTHER_ZONE_COUNT);
  const zones = [];
  for (let i = 1; i <= OTHER_ZONE_COUNT; i++) {
    const size = i === OTHER_ZONE_COUNT ? otherSeats - baseSize * (OTHER_ZONE_COUNT - 1) : baseSize;
    zones.push({ zoneCode: `Z${String(i).padStart(2, '0')}`, size });
  }
  return zones;
}

export function buildSeats() {
  const zones = [{ zoneCode: HOT_ZONE, size: TICKET_LIMIT }].concat(buildOtherZones());
  const seats = [];
  for (const zone of zones) {
    for (let n = 1; n <= zone.size; n++)
      seats.push({ zoneCode: zone.zoneCode, seatNumber: `${zone.zoneCode}-${String(n).padStart(4, '0')}` });
  }
  return seats;
}

function postCreated(path, body, adminToken) {
  const response = postJson(path, body, withTags(authHeaders(adminToken), 'setup'));
  const created = parseJson(response);
  if (response.status !== 201 || !created || typeof created.id !== 'string')
    fail(`setup failed: ${describeFailure('POST', path, response)}`);
  return created.id;
}

export function getOk(path, params) {
  const response = get(path, params);
  const body = parseJson(response);
  if (response.status !== 200 || body === undefined) fail(`setup failed: ${describeFailure('GET', path, response)}`);
  return body;
}

/**
 * 建立場館、2000 席座位圖、活動（販售中、非排隊模式）與 20 個票種；任一步驟非 2xx 就 `fail()`（design 決策 5）。
 * `countTicketQuantity` 為 null 時建座位票 scenario 的票種組合（HOT + Z01–Z19），否則建數量票組合（Z01–Z19 + GA）。
 */
export function createLoadTestEvent({ adminToken, scenarioLabel, countTicketQuantity }) {
  const nowMs = Date.now();
  const nowIso = new Date(nowMs).toISOString().replace(/\.\d{3}Z$/, 'Z');
  const venueId = postCreated('/api/admin/venues', { name: `[LoadTest] venue ${nowIso}` }, adminToken);
  const seatMapId = postCreated(`/api/admin/venues/${venueId}/seat-maps`, { seats: buildSeats() }, adminToken);
  const eventId = postCreated('/api/admin/events', {
    title: `[LoadTest] ${scenarioLabel} ${nowIso}`,
    startAtUtc: new Date(nowMs + EVENT_START_OFFSET_MS).toISOString(),
    venueId,
    seatMapId,
    salesStartAtUtc: new Date(nowMs - SALES_START_OFFSET_MS).toISOString(),
  }, adminToken);

  const otherZoneCodes = buildOtherZones().map((zone) => zone.zoneCode);
  const ticketTypePath = `/api/admin/events/${eventId}/ticket-types`;
  let targetTicketTypeId;
  if (countTicketQuantity === null) {
    targetTicketTypeId = postCreated(ticketTypePath, { zoneCode: HOT_ZONE, price: TICKET_PRICE, requiresSeat: true }, adminToken);
  }
  for (const zoneCode of otherZoneCodes)
    postCreated(ticketTypePath, { zoneCode, price: TICKET_PRICE, requiresSeat: true }, adminToken);
  if (countTicketQuantity !== null) {
    targetTicketTypeId = postCreated(ticketTypePath, {
      zoneCode: COUNT_TICKET_ZONE,
      price: TICKET_PRICE,
      requiresSeat: false,
      availableQuantity: countTicketQuantity,
    }, adminToken);
  }

  return { eventId, targetTicketTypeId };
}

/** 以公開 `GET /api/events` 核對活動為販售中、非排隊模式；不符就 `fail()`。 */
export function assertEventOnSale(eventId) {
  const events = getOk('/api/events', withTags({}, 'setup'));
  const event = Array.isArray(events) ? events.find((e) => e.id === eventId) : undefined;
  if (!event) fail(`setup failed: event ${eventId} not found in GET /api/events`);
  const now = Date.now();
  const isOnSale = event.salesStartAtUtc !== null && Date.parse(event.salesStartAtUtc) <= now &&
    event.salesEndAtUtc === null && now < Date.parse(event.startAtUtc);
  if (event.isQueueModeEnabled !== false || !isOnSale)
    fail(`setup failed: event ${eventId} is not on sale without queue mode (isQueueModeEnabled=${event.isQueueModeEnabled}, salesStartAtUtc=${event.salesStartAtUtc}, salesEndAtUtc=${event.salesEndAtUtc}, startAtUtc=${event.startAtUtc})`);
}

export function getTicketTypes(eventId) {
  return getOk(`/api/events/${eventId}/ticket-types`, withTags({}, 'setup'));
}

export function getSeats(eventId) {
  return getOk(`/api/events/${eventId}/seats`, withTags({}, 'setup'));
}
