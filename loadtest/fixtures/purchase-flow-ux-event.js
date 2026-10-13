// purchase-flow-ux 瀏覽器量測用的活動（openspec/changes/purchase-flow-ux design.md 決策 6「量測資料」、tasks.md 1.0）。
// 只透過既有 API 建立資料，不寫 SQL；不修改 lib 與既有壓測腳本。
import { fail } from 'k6';
import { assertEventOnSale, getOk, getSeats, getTicketTypes } from '../lib/admin-api.js';
import { authHeaders, describeFailure, parseJson, postJson, withTags } from '../lib/http.js';
import { adminTokens, buyerTokens, validateTokens } from '../lib/tokens.js';

const TITLE_PREFIX = '[PurchaseFlowUx] ';
// 一次完整流程最多 2 輪（tasks.md 1.2 與 4.2），6 可容納 2 次前置失敗重跑；失敗的執行同樣計入。
const RETENTION_LIMIT = 6;
// 1280px 寬時購票欄每列約 16 席，400 席約 840px 高，大於 700px 的 iframe 高度（design.md 決策 6）。
const SEAT_ZONE = 'A';
const SEAT_COUNT = 400;
const COUNT_TICKET_ZONE = 'GA';
const COUNT_TICKET_QUANTITY = 100;
const TICKET_PRICE = 1000;
const SALES_START_OFFSET_MS = 60_000;
const EVENT_START_OFFSET_MS = 24 * 60 * 60 * 1000;

export const options = {
  vus: 1,
  iterations: 1,
  setupTimeout: '120s',
};

// 建立與核對都放在 setup()：k6 在 default function 內 fail() 只中止該次 iteration，exit code 仍為 0；
// setup() 拋出例外才會讓 k6 以非 0 結束（tasks.md 1.0「核對」要求任一不符就非 0 結束）。
export default function () {}

// 失敗時只輸出方法、路徑、狀態碼與截斷的回應本文（describeFailure），不含 token 與 request 本文。
function postOk(path, body, token) {
  const response = postJson(path, body, withTags(authHeaders(token), 'fixture'));
  if (response.status < 200 || response.status >= 300) fail(`fixture failed: ${describeFailure('POST', path, response)}`);
  return parseJson(response);
}

function postCreatedId(path, body, token) {
  const created = postOk(path, body, token);
  if (!created || typeof created.id !== 'string') fail(`fixture failed: POST ${path} returned no id`);
  return created.id;
}

function countRetainedEvents(adminToken) {
  const events = getOk('/api/admin/events', withTags(authHeaders(adminToken), 'fixture'));
  if (!Array.isArray(events)) fail('fixture failed: GET /api/admin/events did not return an array');
  return events.filter((e) => typeof e.title === 'string' && e.title.startsWith(TITLE_PREFIX)).length;
}

function buildSeats() {
  const seats = [];
  for (let n = 1; n <= SEAT_COUNT; n++) seats.push({ zoneCode: SEAT_ZONE, seatNumber: String(n) });
  return seats;
}

function findSeat(seats, seatNumber) {
  const seat = seats.find((s) => s.zoneCode === SEAT_ZONE && s.seatNumber === seatNumber);
  if (!seat) fail(`fixture failed: seat ${SEAT_ZONE}-${seatNumber} not found`);
  return seat;
}

function placeSeatOrder(buyerToken, eventSeatId, ticketTypeId) {
  return postCreatedId('/api/orders', { selections: [{ eventSeatId, ticketTypeId, quantity: 1 }] }, buyerToken);
}

function verifyFixture(eventId) {
  assertEventOnSale(eventId);
  const seats = getSeats(eventId);
  if (!Array.isArray(seats)) fail('fixture failed: seats response is not an array');
  const zoneSeats = seats.filter((s) => s.zoneCode === SEAT_ZONE);
  if (zoneSeats.length !== SEAT_COUNT) fail(`fixture failed: zone ${SEAT_ZONE} has ${zoneSeats.length} seats, expected ${SEAT_COUNT}`);
  if (findSeat(zoneSeats, '1').status !== 'Sold') fail(`fixture failed: seat ${SEAT_ZONE}-1 is not Sold`);
  if (!zoneSeats.some((s) => s.status === 'Available')) fail(`fixture failed: zone ${SEAT_ZONE} has no Available seat`);

  const ticketTypes = getTicketTypes(eventId);
  if (!Array.isArray(ticketTypes)) fail('fixture failed: ticket types response is not an array');
  if (!ticketTypes.some((t) => t.requiresSeat === true) || !ticketTypes.some((t) => t.requiresSeat === false))
    fail('fixture failed: event needs both seat and count ticket types');
}

export function setup() {
  // 上限檢查與建立之間不是原子操作，必須由 wrapper 先取得跨執行的互斥鎖（tasks.md 1.0「並行保護」）；
  // k6 無法連 Redis，這裡只擋「沒經過 wrapper 直接執行」的誤用。
  if (!/^[0-9a-f]{32}$/.test(__ENV.PFUX_FIXTURE_LOCK_TOKEN || '')) fail('fixture failed: run via loadtest/fixtures/run-purchase-flow-ux-event.sh (cross-run lock required)');
  const tokenError = validateTokens(2);
  if (tokenError !== null) fail(`fixture failed: ${tokenError}`);
  const adminToken = adminTokens[0];

  // 先檢查保留上限，達到上限時不建立任何資料（tasks.md 1.0「保留上限」）。
  const retainedCount = countRetainedEvents(adminToken);
  if (retainedCount >= RETENTION_LIMIT) fail(`PURCHASE_FLOW_UX_RETENTION_LIMIT_REACHED count=${retainedCount}`);

  const nowMs = Date.now();
  const nowIso = new Date(nowMs).toISOString().replace(/\.\d{3}Z$/, 'Z');
  const venueId = postCreatedId('/api/admin/venues', { name: `${TITLE_PREFIX}venue ${nowIso}` }, adminToken);
  const seatMapId = postCreatedId(`/api/admin/venues/${venueId}/seat-maps`, { seats: buildSeats() }, adminToken);
  const eventId = postCreatedId('/api/admin/events', {
    title: `${TITLE_PREFIX}${nowIso}`,
    startAtUtc: new Date(nowMs + EVENT_START_OFFSET_MS).toISOString(),
    venueId,
    seatMapId,
    salesStartAtUtc: new Date(nowMs - SALES_START_OFFSET_MS).toISOString(),
  }, adminToken);
  const ticketTypePath = `/api/admin/events/${eventId}/ticket-types`;
  const seatTicketTypeId = postCreatedId(ticketTypePath, { zoneCode: SEAT_ZONE, price: TICKET_PRICE, requiresSeat: true }, adminToken);
  postCreatedId(ticketTypePath, {
    zoneCode: COUNT_TICKET_ZONE,
    price: TICKET_PRICE,
    requiresSeat: false,
    availableQuantity: COUNT_TICKET_QUANTITY,
  }, adminToken);

  // 第 1 席付款成為永久 Sold；第 2 席不付款成為 Held，持有期限到期會釋出，所以只核對 Sold。
  const seats = getSeats(eventId);
  const soldOrderId = placeSeatOrder(buyerTokens[0], findSeat(seats, '1').eventSeatId, seatTicketTypeId);
  postOk(`/api/orders/${soldOrderId}/confirm`, {}, buyerTokens[0]);
  placeSeatOrder(buyerTokens[1], findSeat(seats, '2').eventSeatId, seatTicketTypeId);

  verifyFixture(eventId);
  console.log(`PURCHASE_FLOW_UX_EVENT_ID=${eventId}`);
}
