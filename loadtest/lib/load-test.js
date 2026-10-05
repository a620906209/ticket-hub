// 兩支壓測腳本共用的 options、setup、下單、verify 與 handleSummary（k6-load-test design.md 決策 5–7、9–11）。
import http from 'k6/http';
import exec from 'k6/execution';
import { fail, sleep } from 'k6';
import {
  API_BASE_URL,
  BUYER_COUNT,
  HOT_ZONE,
  OTHER_ZONE_COUNT,
  PLACE_ORDER_MAX_DURATION,
  SETUP_TIMEOUT,
  START_DELAY_MS,
  TICKET_LIMIT,
  TOTAL_SEATS,
  VERIFY_START_TIME,
  hasFault,
  tamperToken,
} from './config.js';
import { assertEventOnSale, createLoadTestEvent, getSeats, getTicketTypes } from './admin-api.js';
import { authHeaders, get, parseJson, postJson, withTags } from './http.js';
import {
  buyerIterationsCompleted,
  confirmFailed,
  initializeCounters,
  ordersCreated,
  oversellCheckFailed,
  placeOrder5xx,
  placeOrderConflict,
  placeOrderSendOffsetMs,
  placeOrderUnexpected,
  verifyQuantitySold,
} from './metrics.js';
import { PLACE_ORDER_DURATION_METRIC, evaluateRunResult, readMetricValue } from './result.js';
import { adminTokens, buyerTokens, validateTokens } from './tokens.js';
import { AVAILABLE_SEAT_STATUS, evaluateVerification } from './verify.js';

const TOTAL_TICKET_TYPES = OTHER_ZONE_COUNT + 1;

/** init 階段呼叫：試讀目標 summary 檔，存在就回傳 true（open() 只能在 init 階段使用）。 */
export function detectExistingSummary(settings) {
  if (!settings.isOfficialRun) return false;
  try {
    open(settings.summaryPath);
    return true;
  } catch (_) {
    return false;
  }
}

export function createOptions(settings) {
  const isCountTicket = settings.scenario === 'count-ticket';
  return {
    // 故障注入只能讓情況變壞（design 決策 9）：setup-timeout 讓 setup 必然逾時（1s 不夠：API 熱機後 setup 可在 1 秒內完成，2026-10-05 實測）、p95 讓門檻更嚴。
    setupTimeout: hasFault(settings, 'setup-timeout') ? '1ms' : SETUP_TIMEOUT,
    summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
    scenarios: {
      'place-order': {
        executor: 'per-vu-iterations',
        vus: BUYER_COUNT,
        iterations: 1,
        maxDuration: PLACE_ORDER_MAX_DURATION,
        gracefulStop: '0s',
        exec: 'placeOrder',
      },
      verify: {
        executor: 'per-vu-iterations',
        vus: 1,
        iterations: 1,
        startTime: VERIFY_START_TIME,
        exec: 'verify',
      },
    },
    thresholds: {
      [PLACE_ORDER_DURATION_METRIC]: [hasFault(settings, 'p95') ? 'p(95)<1' : 'p(95)<500'],
      orders_created: isCountTicket
        ? [`count<=${TICKET_LIMIT}`, `count>=${TICKET_LIMIT}`]
        : [`count<=${TICKET_LIMIT}`, 'count>=1'],
      place_order_5xx: ['count==0'],
      place_order_unexpected: ['count==0'],
      confirm_failed: ['count==0'],
      buyer_iterations_completed: [`count==${BUYER_COUNT}`],
      oversell_check_failed: ['count==0'],
    },
  };
}

function adminToken(settings) {
  return hasFault(settings, 'bad-admin-token') ? tamperToken(adminTokens[0]) : adminTokens[0];
}

function assertCountTicketBoundary(ticketTypes, targetTicketTypeId, expectedQuantity) {
  const target = ticketTypes.find((t) => t.id === targetTicketTypeId);
  if (!target || target.requiresSeat !== false || target.availableQuantity !== expectedQuantity)
    fail(`setup failed: count ticket type mismatch (requiresSeat=${target && target.requiresSeat}, availableQuantity=${target && target.availableQuantity}, expected=${expectedQuantity})`);
}

/** design 決策 5「座位票的 50 席邊界」：核對票種、HOT 分區 50 席，回傳交給 VU 的座位池。 */
function buildSeatPool(ticketTypes, seats, targetTicketTypeId) {
  const target = ticketTypes.find((t) => t.id === targetTicketTypeId);
  if (!target || target.zoneCode !== HOT_ZONE || target.requiresSeat !== true)
    fail(`setup failed: seat ticket type mismatch (zoneCode=${target && target.zoneCode}, requiresSeat=${target && target.requiresSeat})`);

  const hotSeats = seats.filter((seat) => seat.zoneCode === HOT_ZONE);
  const hotSeatIds = new Set(hotSeats.map((seat) => seat.eventSeatId));
  if (hotSeats.length !== TICKET_LIMIT || hotSeatIds.size !== TICKET_LIMIT ||
      hotSeats.some((seat) => seat.status !== AVAILABLE_SEAT_STATUS))
    fail(`setup failed: ${HOT_ZONE} zone must have ${TICKET_LIMIT} distinct available seats (seats=${hotSeats.length}, distinct=${hotSeatIds.size})`);

  const seatPool = Array.from(hotSeatIds);
  // 集合相等：座位池恰好是核對過的 50 個 id，VU 只從這裡選位。
  if (seatPool.length !== hotSeatIds.size || seatPool.some((id) => !hotSeatIds.has(id)))
    fail('setup failed: seat pool does not equal the HOT seat id set');
  return seatPool;
}

export function runSetup(settings, isSummaryExisting) {
  if (settings.errors.length > 0) fail(`setup aborted: ${settings.errors.join('; ')}`);
  if (isSummaryExisting) fail(`setup aborted: ${settings.summaryPath} already exists; results must not be overwritten`);
  initializeCounters();

  const tokenError = validateTokens(BUYER_COUNT);
  if (tokenError !== null) fail(`setup aborted: ${tokenError}`);

  const isCountTicket = settings.scenario === 'count-ticket';
  const countTicketQuantity = hasFault(settings, 'quantity51') ? TICKET_LIMIT + 1 : TICKET_LIMIT;
  const { eventId, targetTicketTypeId } = createLoadTestEvent({
    adminToken: adminToken(settings),
    scenarioLabel: isCountTicket ? 'count' : 'seat',
    countTicketQuantity: isCountTicket ? countTicketQuantity : null,
  });

  assertEventOnSale(eventId);
  const ticketTypes = getTicketTypes(eventId);
  const seats = getSeats(eventId);
  if (!Array.isArray(ticketTypes) || ticketTypes.length !== TOTAL_TICKET_TYPES)
    fail(`setup failed: expected ${TOTAL_TICKET_TYPES} ticket types, got ${Array.isArray(ticketTypes) ? ticketTypes.length : 'non-array'}`);
  if (!Array.isArray(seats) || seats.length !== TOTAL_SEATS)
    fail(`setup failed: expected ${TOTAL_SEATS} seats, got ${Array.isArray(seats) ? seats.length : 'non-array'}`);

  let seatPool = [];
  if (isCountTicket) assertCountTicketBoundary(ticketTypes, targetTicketTypeId, countTicketQuantity);
  else seatPool = buildSeatPool(ticketTypes, seats, targetTicketTypeId);

  console.log(`setup ok: event=${eventId} seats=${seats.length} ticketTypes=${ticketTypes.length} ` +
    `queueMode=false onSale=true seatPool=${seatPool.length}`);
  return { eventId, targetTicketTypeId, seatPool, startAt: Date.now() + START_DELAY_MS };
}

function confirmOrder(orderId, params) {
  if (typeof orderId !== 'string') {
    confirmFailed.add(1);
    return;
  }
  const response = http.post(`${API_BASE_URL}/api/orders/${orderId}/confirm`, null, withTags(params, 'confirm-order'));
  if (response.status < 200 || response.status >= 300) confirmFailed.add(1);
}

/** `buildSelection(data)` 回傳單一 selection；VU 對應 token 用 scenario 內不重複的 iteration 序號（design 決策 6）。 */
export function runPlaceOrder(settings, data, buildSelection) {
  const buyerToken = buyerTokens[exec.scenario.iterationInTest];
  const params = authHeaders(hasFault(settings, 'bad-buyer-token') ? tamperToken(buyerToken) : buyerToken);
  const selection = buildSelection(data);

  const waitMs = data.startAt - Date.now();
  if (waitMs > 0) sleep(waitMs / 1000);
  placeOrderSendOffsetMs.add(Date.now() - data.startAt);
  const response = postJson('/api/orders', { selections: [selection] }, withTags(params, 'place-order'));

  if (response.status === 201) {
    ordersCreated.add(1);
    const created = parseJson(response);
    confirmOrder(created ? created.id : undefined, params);
  } else if (response.status === 409) {
    placeOrderConflict.add(1);
  } else {
    // 「其餘回應皆為 409」：401／400／404／429、5xx 與網路錯誤（status 0）都算非預期（design 決策 7）。
    placeOrderUnexpected.add(1);
    if (response.status >= 500) placeOrder5xx.add(1);
  }
  buyerIterationsCompleted.add(1);
}

export function runVerify(settings, data) {
  const params = withTags(authHeaders(adminToken(settings)), 'verify');
  const publicParams = withTags({}, 'verify');
  const paths = {
    salesReport: `/api/admin/events/${data.eventId}/sales-report`,
    ticketTypes: `/api/events/${data.eventId}/ticket-types`,
    seats: `/api/events/${data.eventId}/seats`,
  };
  const responses = {
    salesReport: get(paths.salesReport, params),
    ticketTypes: get(paths.ticketTypes, publicParams),
    seats: get(paths.seats, publicParams),
  };
  const fetchErrors = Object.keys(responses)
    .filter((key) => responses[key].status !== 200)
    .map((key) => `GET ${paths[key]} -> ${responses[key].status}`);
  if (fetchErrors.length > 0) {
    oversellCheckFailed.add(1);
    console.log(`verify: QuantitySold=unavailable reasons=${JSON.stringify(fetchErrors)}`);
    return;
  }

  const result = evaluateVerification({
    scenario: settings.scenario,
    targetTicketTypeId: data.targetTicketTypeId,
    ticketLimit: TICKET_LIMIT,
    hotZone: HOT_ZONE,
    salesReport: parseJson(responses.salesReport),
    ticketTypes: parseJson(responses.ticketTypes),
    seats: parseJson(responses.seats),
  });
  if (result.quantitySold !== null) verifyQuantitySold.add(result.quantitySold);
  if (result.reasons.length > 0) oversellCheckFailed.add(1);
  console.log(`verify: QuantitySold=${result.quantitySold === null ? 'unavailable' : result.quantitySold} reasons=${JSON.stringify(result.reasons)}`);
}

function formatValue(metrics, name, field) {
  const value = readMetricValue(metrics, name, field);
  return value === undefined ? '缺少' : String(Math.round(value * 100) / 100);
}

function renderStdoutSummary(settings, metrics, runVerdict, fileNote) {
  const lines = [
    `scenario=${settings.scenario} build=${settings.build} run=${settings.run !== null ? settings.run : settings.fault !== null ? 'n/a' : 'invalid'} fault=${settings.fault === null ? 'none' : settings.fault}`,
    `place-order p95=${formatValue(metrics, PLACE_ORDER_DURATION_METRIC, 'p(95)')}ms p99=${formatValue(metrics, PLACE_ORDER_DURATION_METRIC, 'p(99)')}ms`,
    `orders_created=${formatValue(metrics, 'orders_created', 'count')} conflict=${formatValue(metrics, 'place_order_conflict', 'count')} ` +
      `5xx=${formatValue(metrics, 'place_order_5xx', 'count')} unexpected=${formatValue(metrics, 'place_order_unexpected', 'count')} ` +
      `confirm_failed=${formatValue(metrics, 'confirm_failed', 'count')} completed=${formatValue(metrics, 'buyer_iterations_completed', 'count')}`,
    `verify_quantity_sold=${formatValue(metrics, 'verify_quantity_sold', 'value')} oversell_check_failed=${formatValue(metrics, 'oversell_check_failed', 'count')} ` +
      `send_offset_max=${formatValue(metrics, 'place_order_send_offset_ms', 'max')}ms`,
    `runVerdict.passed=${runVerdict.passed}`,
  ].concat(runVerdict.reasons.map((reason) => `  - ${reason}`));
  lines.push(fileNote);
  return `\n${lines.join('\n')}\n`;
}

/** 不得拋例外（tasks 4.5）：出錯時改寫一份 runVerdict.passed=false 的 summary 並輸出錯誤到 stdout。 */
export function buildSummaryOutputs(settings, isSummaryExisting, data) {
  try {
    const metrics = data && data.metrics ? data.metrics : {};
    const runVerdict = evaluateRunResult(data);
    const durationMs = data && data.state && typeof data.state.testRunDurationMs === 'number' ? data.state.testRunDurationMs : 0;
    const runInfo = {
      scenario: settings.scenario,
      apiBuild: settings.build,
      run: settings.run,
      fault: settings.fault,
      startedAtUtc: new Date(Date.now() - durationMs).toISOString(),
      eventId: data && data.setup_data ? data.setup_data.eventId : null,
    };
    // 目標檔已存在時（setup 已因此中止）不寫檔，避免覆寫既有正式結果（design 決策 10）。
    if (isSummaryExisting) {
      return { stdout: renderStdoutSummary(settings, metrics, runVerdict, `summary not written: ${settings.summaryPath} already exists`) };
    }
    const summary = { runInfo, runVerdict, metrics, state: data.state, options: data.options };
    return {
      [settings.summaryPath]: JSON.stringify(summary, null, 2),
      stdout: renderStdoutSummary(settings, metrics, runVerdict, `summary written: ${settings.summaryPath}`),
    };
  } catch (error) {
    // 不重拋：handleSummary 拋例外時 k6 照樣 exit 0 且不寫檔（2026-10-05 實測），反而更不明顯。
    // 改寫一份明確判失敗的 summary，讓彙整讀到「未通過」而非只在 stdout 留一行。
    const reason = `handleSummary error: ${String(error)}`;
    if (isSummaryExisting) return { stdout: `\n${reason}\n` };
    const failedSummary = {
      runInfo: { scenario: settings.scenario, apiBuild: settings.build, run: settings.run, fault: settings.fault },
      runVerdict: { passed: false, reasons: [reason] },
    };
    return {
      [settings.summaryPath]: JSON.stringify(failedSummary, null, 2),
      stdout: `\n${reason}\nsummary written (failed): ${settings.summaryPath}\n`,
    };
  }
}
