// 無競爭基準（order-placement-p95-optimization design.md 決策 3）的可測邏輯：設定、options、指派、分區唯一性、判定與輸出。
// 不呼叫 HTTP，k6 端的流程在 baseline.js；這裡的函式都以 tests/baseline.test.js 的假資料自我測試。
import { b64decode } from 'k6/encoding';
import { OUTPUT_DIR, SETUP_TIMEOUT, applyMeasurePrefix, parseMeasureTag } from './config.js';
import { PLACE_ORDER_DURATION_METRIC, readMetricValue } from './result.js';

export const BASELINE_ORDER_COUNT = 50;
const SCENARIO_BY_TICKET = { count: 'count-ticket', seat: 'seat-ticket' };
const RATE_LIMITED_MARKER = '被限流，量測無效';

/**
 * 解析 `LT_BASELINE_TICKET`／`LT_MEASURE_TAG`。回傳的物件沿用 `parseRunSettings` 的欄位，讓 `runSetup`／
 * `detectExistingSummary` 可以直接使用；不讀 `LT_API_BUILD`／`LT_RUN`（design 決策 3）。
 */
export function parseBaselineSettings(env) {
  const errors = [];
  const isTicketValid = Object.prototype.hasOwnProperty.call(SCENARIO_BY_TICKET, env.LT_BASELINE_TICKET);
  if (!isTicketValid) errors.push('LT_BASELINE_TICKET must be one of count, seat');
  const ticket = isTicketValid ? env.LT_BASELINE_TICKET : 'invalid';

  const measureTag = parseMeasureTag(env, { isRequired: true });
  errors.push(...measureTag.errors);
  // 基準一定要有標籤：缺標籤時失敗 summary 也用 invalid 前綴，不留下看似正式的無前綴檔名。
  const prefixTag = measureTag.tag === null ? { ...measureTag, isInvalid: true } : measureTag;

  return {
    scenario: isTicketValid ? SCENARIO_BY_TICKET[ticket] : 'invalid',
    ticket,
    fault: null,
    measureTag: measureTag.tag,
    summaryPath: `${OUTPUT_DIR}/${applyMeasurePrefix(`baseline-${ticket}-summary.json`, prefixTag)}`,
    isSummaryOverwriteProtected: errors.length === 0,
    errors,
  };
}

export function createBaselineOptions() {
  return {
    setupTimeout: SETUP_TIMEOUT,
    summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
    scenarios: {
      'baseline-order': {
        // 1 個 VU 跑 50 次即依序送出：同一時間只有一筆在伺服器上，量到的是無競爭的單筆成本。
        executor: 'per-vu-iterations',
        vus: 1,
        iterations: BASELINE_ORDER_COUNT,
        maxDuration: '120s',
        exec: 'placeOrder',
      },
    },
    thresholds: {
      baseline_non_201: ['count==0'],
      baseline_orders_created: [`count==${BASELINE_ORDER_COUNT}`],
      // 永遠成立；只為了讓 k6 在 summary 輸出 place-order 子指標（不宣告 threshold 的子指標不會出現）。
      [PLACE_ORDER_DURATION_METRIC]: ['max>=0'],
    },
  };
}

/** 第 i 筆用第 i 個買家 token 與第 i 個座位：50 筆互不重複，不會撞位、也不會吃到同一會員的限流額度。 */
export function assignBaselineOrder(scenario, iteration, data) {
  const selection = scenario === 'seat-ticket'
    ? { eventSeatId: data.seatPool[iteration], ticketTypeId: data.targetTicketTypeId, quantity: 1 }
    : { ticketTypeId: data.targetTicketTypeId, quantity: 1 };
  return { buyerTokenIndex: iteration, selection };
}

/**
 * 確認前 `count` 個買家 token 屬於 `count` 個不同會員（限流分區鍵是 JWT `sub`，design 決策 3）。
 * 只解 payload、不驗簽章；錯誤訊息只帶 token 序號，不含 token 或 `sub` 內容。回傳錯誤訊息，沒問題時回傳 null。
 */
export function validateDistinctSubjects(tokens, count) {
  if (!tokens || tokens.length < count) return `need at least ${count} buyer tokens for the baseline`;
  const subjects = new Set();
  for (let i = 0; i < count; i++) {
    const subject = readSubject(tokens[i]);
    if (subject === null) return `buyer token #${i} has no readable string sub claim`;
    if (subjects.has(subject)) return `buyer token #${i} shares a rate-limit partition (sub) with an earlier token`;
    subjects.add(subject);
  }
  return null;
}

function readSubject(token) {
  if (typeof token !== 'string') return null;
  const segments = token.split('.');
  if (segments.length !== 3) return null;
  let payload;
  try {
    payload = JSON.parse(b64decode(segments[1], 'rawurl', 's'));
  } catch (_) {
    // 解碼或 JSON 失敗都視為無法取得 sub，由呼叫端以 token 序號回報並中止 setup。
    return null;
  }
  const subject = payload !== null && typeof payload === 'object' ? payload.sub : undefined;
  return typeof subject === 'string' && subject !== '' ? subject : null;
}

function readCount(metrics, name, reasons) {
  const value = readMetricValue(metrics, name, 'count');
  if (value === undefined) reasons.push(`missing counter: ${name}`);
  return value;
}

/** 50 筆皆 201 才有效；429 另外標示，與其他失敗區分（LT-MEASURE-006、013）。 */
export function evaluateBaselineResult(metrics) {
  const reasons = [];
  const created = readCount(metrics, 'baseline_orders_created', reasons);
  const non201 = readCount(metrics, 'baseline_non_201', reasons);
  const rateLimited = readCount(metrics, 'baseline_rate_limited', reasons);
  const p50 = readMetricValue(metrics, PLACE_ORDER_DURATION_METRIC, 'med');
  const p95 = readMetricValue(metrics, PLACE_ORDER_DURATION_METRIC, 'p(95)');

  const isRateLimited = rateLimited !== undefined && rateLimited > 0;
  if (isRateLimited) reasons.push(`${RATE_LIMITED_MARKER}: ${rateLimited} response(s) were 429`);
  if (non201 !== undefined && non201 > 0) reasons.push(`${non201} response(s) were not 201`);
  if (created !== undefined && created !== BASELINE_ORDER_COUNT) reasons.push(`orders created ${created}, expected ${BASELINE_ORDER_COUNT}`);
  if (p50 === undefined || p95 === undefined) reasons.push(`missing place-order duration (${PLACE_ORDER_DURATION_METRIC})`);

  return {
    isValid: reasons.length === 0,
    isRateLimited,
    p50: p50 === undefined ? null : p50,
    p95: p95 === undefined ? null : p95,
    reasons,
  };
}

function renderBaselineStdout(settings, baseline, fileNote) {
  const lines = [
    `baseline ticket=${settings.ticket} tag=${settings.measureTag === null ? 'invalid' : settings.measureTag}`,
    `place-order p50=${baseline.p50 === null ? '缺少' : baseline.p50}ms p95=${baseline.p95 === null ? '缺少' : baseline.p95}ms`,
    `baseline.isValid=${baseline.isValid} isRateLimited=${baseline.isRateLimited}`,
  ].concat(baseline.reasons.map((reason) => `  - ${reason}`));
  lines.push(fileNote);
  return `\n${lines.join('\n')}\n`;
}

/** 不得拋例外（比照 buildSummaryOutputs）：出錯時改寫一份 isValid=false 的 summary。目標檔已存在時不寫檔。 */
export function buildBaselineSummaryOutputs(settings, isSummaryExisting, data) {
  try {
    const metrics = data && data.metrics ? data.metrics : {};
    const baseline = evaluateBaselineResult(metrics);
    if (isSummaryExisting)
      return { stdout: renderBaselineStdout(settings, baseline, `summary not written: ${settings.summaryPath} already exists`) };
    const runInfo = {
      scenario: settings.scenario,
      ticket: settings.ticket,
      measureTag: settings.measureTag,
      eventId: data && data.setup_data ? data.setup_data.eventId : null,
    };
    const summary = { runInfo, baseline, metrics, state: data.state, options: data.options };
    return {
      [settings.summaryPath]: JSON.stringify(summary, null, 2),
      stdout: renderBaselineStdout(settings, baseline, `summary written: ${settings.summaryPath}`),
    };
  } catch (error) {
    const reason = `handleSummary error: ${String(error)}`;
    if (isSummaryExisting) return { stdout: `\n${reason}\n` };
    const failedSummary = {
      runInfo: { scenario: settings.scenario, ticket: settings.ticket, measureTag: settings.measureTag },
      baseline: { isValid: false, isRateLimited: false, p50: null, p95: null, reasons: [reason] },
    };
    return {
      [settings.summaryPath]: JSON.stringify(failedSummary, null, 2),
      stdout: `\n${reason}\nsummary written (failed): ${settings.summaryPath}\n`,
    };
  }
}
