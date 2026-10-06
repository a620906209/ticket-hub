// [LT-MEASURE-005] [LT-MEASURE-006] [LT-MEASURE-009] [LT-MEASURE-013] [LT-MEASURE-014]
// 無競爭基準的純函式自我測試（order-placement-p95-optimization design.md 決策 3）。
// 基準的用途是「單筆鎖內成本」：只要有一筆被限流、撞位或分區重複，量到的就不是無競爭成本，必須判無效。
import { check } from 'k6';
import { b64encode } from 'k6/encoding';
import {
  BASELINE_ORDER_COUNT,
  assignBaselineOrder,
  buildBaselineSummaryOutputs,
  createBaselineOptions,
  evaluateBaselineResult,
  parseBaselineSettings,
  validateDistinctSubjects,
} from '../lib/baseline.js';
import { TICKET_LIMIT } from '../lib/config.js';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate==1'] },
};

function encodeSegment(text) {
  return b64encode(text, 'rawurl');
}

function fakeJwt(payload, signature = 'sig') {
  return `${encodeSegment(JSON.stringify({ alg: 'HS256', typ: 'JWT' }))}.${encodeSegment(JSON.stringify(payload))}.${signature}`;
}

function distinctTokens(count) {
  const tokens = [];
  for (let i = 0; i < count; i++) tokens.push(fakeJwt({ sub: `member-${i}` }));
  return tokens;
}

function counter(count) {
  return { type: 'counter', contains: 'default', values: { count, rate: 1 } };
}

function baselineMetrics({ created = 50, non201 = 0, rateLimited = 0, includeDuration = true } = {}) {
  const metrics = {
    baseline_orders_created: counter(created),
    baseline_non_201: counter(non201),
    baseline_rate_limited: counter(rateLimited),
  };
  if (includeDuration) {
    metrics['http_req_duration{name:place-order}'] = {
      type: 'trend',
      contains: 'time',
      values: { avg: 40, min: 20, med: 35.5, max: 90, 'p(90)': 60, 'p(95)': 72.25, 'p(99)': 88 },
    };
  }
  return metrics;
}

function testSettings() {
  for (const ticket of ['count', 'seat']) {
    const settings = parseBaselineSettings({ LT_BASELINE_TICKET: ticket, LT_MEASURE_TAG: 'before' });
    check(settings, {
      [`${ticket}: summary name is measure-<tag>-baseline-<ticket>-summary.json`]: (s) =>
        s.errors.length === 0 && s.summaryPath === `/output/measure-before-baseline-${ticket}-summary.json`,
      [`${ticket}: maps to load-test scenario`]: (s) => s.scenario === (ticket === 'count' ? 'count-ticket' : 'seat-ticket'),
      [`${ticket}: overwrite-protected and not a fault run`]: (s) => s.isSummaryOverwriteProtected === true && s.fault === null,
    });
  }

  for (const ticket of [undefined, '', 'COUNT', 'seats', 'both']) {
    check(parseBaselineSettings({ LT_BASELINE_TICKET: ticket, LT_MEASURE_TAG: 'before' }), {
      [`LT_BASELINE_TICKET=${JSON.stringify(ticket)} rejected`]: (s) =>
        s.errors.some((e) => e.includes('LT_BASELINE_TICKET')) && s.isSummaryOverwriteProtected === false,
    });
  }

  check(parseBaselineSettings({ LT_BASELINE_TICKET: 'seat' }), {
    'missing LT_MEASURE_TAG rejected (LT-MEASURE-009)': (s) => s.errors.some((e) => e.includes('LT_MEASURE_TAG')),
    'missing tag is not overwrite-protected (setup aborts, nothing written as valid)': (s) => s.isSummaryOverwriteProtected === false,
    'missing tag summary uses invalid prefix, never the bare name': (s) => s.summaryPath === '/output/measure-invalid-baseline-seat-summary.json',
  });
  check(parseBaselineSettings({ LT_BASELINE_TICKET: 'seat', LT_MEASURE_TAG: '../x' }), {
    'invalid LT_MEASURE_TAG rejected and not placed in path': (s) =>
      s.errors.some((e) => e.includes('LT_MEASURE_TAG')) && !s.summaryPath.includes('..'),
  });
}

function testOptions() {
  const baselineOptions = createBaselineOptions();
  const scenarioNames = Object.keys(baselineOptions.scenarios);
  const scenario = baselineOptions.scenarios[scenarioNames[0]];
  check(baselineOptions, {
    'single scenario': () => scenarioNames.length === 1,
    // 1 個 VU 跑完 50 次 = 依序送出；併發會讓它變回競爭量測。
    'vus 1, iterations 50, sequential executor': () =>
      scenario.vus === 1 && scenario.iterations === BASELINE_ORDER_COUNT && BASELINE_ORDER_COUNT === 50 && scenario.executor === 'per-vu-iterations',
    'baseline_non_201 threshold count==0': (o) => JSON.stringify(o.thresholds.baseline_non_201) === JSON.stringify(['count==0']),
    'baseline_orders_created threshold count==50': (o) => JSON.stringify(o.thresholds.baseline_orders_created) === JSON.stringify(['count==50']),
    'place-order duration submetric is declared so p50/p95 appear in summary': (o) =>
      Array.isArray(o.thresholds['http_req_duration{name:place-order}']),
    'p50 and p95 in summary trend stats': (o) => o.summaryTrendStats.includes('med') && o.summaryTrendStats.includes('p(95)'),
  });
}

function testAssignment() {
  const seatPool = [];
  for (let i = 0; i < 50; i++) seatPool.push(`seat-${i}`);
  const data = { targetTicketTypeId: 'tt', seatPool };

  const seatAssignments = [];
  const countAssignments = [];
  for (let i = 0; i < BASELINE_ORDER_COUNT; i++) {
    seatAssignments.push(assignBaselineOrder('seat-ticket', i, data));
    countAssignments.push(assignBaselineOrder('count-ticket', i, data));
  }
  check(null, {
    'seat: 50 distinct buyer token indexes': () => new Set(seatAssignments.map((a) => a.buyerTokenIndex)).size === 50,
    'seat: 50 distinct seats': () => new Set(seatAssignments.map((a) => a.selection.eventSeatId)).size === 50,
    'seat: selection quantity 1 on target ticket type': () =>
      seatAssignments.every((a) => a.selection.quantity === 1 && a.selection.ticketTypeId === 'tt'),
    'count: 50 distinct buyer token indexes': () => new Set(countAssignments.map((a) => a.buyerTokenIndex)).size === 50,
    'count: no seat, quantity 1': () =>
      countAssignments.every((a) => a.selection.eventSeatId === undefined && a.selection.quantity === 1 && a.selection.ticketTypeId === 'tt'),
    // 數量票庫存沿用 createLoadTestEvent 的 TICKET_LIMIT；不足 50 會讓後段收到 409，基準就不是全 201。
    'count: stock is at least 50': () => TICKET_LIMIT >= BASELINE_ORDER_COUNT,
  });
}

function testDistinctSubjects() {
  const tokens = distinctTokens(50);
  check(validateDistinctSubjects(tokens, 50), { '50 distinct subs accepted': (error) => error === null });
  check(validateDistinctSubjects(distinctTokens(500), 50), { 'only first 50 inspected; 500 distinct accepted': (error) => error === null });

  const duplicated = tokens.slice();
  // token 字串不同（簽章不同），但屬於同一會員：現有 validateTokens 抓不到，正是這個檢查要擋的。
  duplicated[7] = fakeJwt({ sub: 'member-3' }, 'other-signature');
  const failures = {
    'different tokens with same sub': duplicated,
    'fewer than 50 tokens': tokens.slice(0, 49),
    'payload not base64url': replaceAt(tokens, 5, `${tokens[5].split('.')[0]}.@@@!!.sig`),
    'payload not json': replaceAt(tokens, 5, `${tokens[5].split('.')[0]}.${encodeSegment('not json member-5')}.sig`),
    'payload missing sub': replaceAt(tokens, 5, fakeJwt({ name: 'member-5' })),
    'sub not string': replaceAt(tokens, 5, fakeJwt({ sub: 12345 })),
    'sub empty string': replaceAt(tokens, 5, fakeJwt({ sub: '' })),
    'two segments': replaceAt(tokens, 5, tokens[5].split('.').slice(0, 2).join('.')),
    'four segments': replaceAt(tokens, 5, `${tokens[5]}.extra`),
    'token not a string': replaceAt(tokens, 5, null),
  };
  for (const name of Object.keys(failures)) {
    const list = failures[name];
    const error = validateDistinctSubjects(list, 50);
    check(error, {
      [`${name}: rejected`]: (e) => typeof e === 'string' && e.length > 0,
      [`${name}: message has no token or sub content`]: (e) =>
        typeof e === 'string' && !/member-|sig|eyJ|12345|@@@/.test(e),
    });
  }
}

function replaceAt(list, index, value) {
  const copy = list.slice();
  copy[index] = value;
  return copy;
}

function testEvaluation() {
  check(evaluateBaselineResult(baselineMetrics()), {
    'all 201: valid': (r) => r.isValid === true && r.isRateLimited === false && r.reasons.length === 0,
    'p50/p95 read from place-order submetric': (r) => r.p50 === 35.5 && r.p95 === 72.25,
  });
  check(evaluateBaselineResult(baselineMetrics({ created: 49, non201: 1 })), {
    'one non-201: invalid': (r) => r.isValid === false && r.isRateLimited === false,
  });
  check(evaluateBaselineResult(baselineMetrics({ created: 49, non201: 1, rateLimited: 1 })), {
    '429: invalid and flagged rate limited': (r) =>
      r.isValid === false && r.isRateLimited === true && r.reasons.some((reason) => reason.includes('被限流，量測無效')),
  });
  check(evaluateBaselineResult(baselineMetrics({ created: 30 })), {
    'fewer than 50 created without non-201 (e.g. iterations cut short): invalid': (r) => r.isValid === false,
  });
  check(evaluateBaselineResult({}), {
    'missing counters: invalid': (r) => r.isValid === false && r.p50 === null && r.p95 === null,
  });
  check(evaluateBaselineResult(baselineMetrics({ includeDuration: false })), {
    'missing duration: invalid': (r) => r.isValid === false,
  });
}

function testSummaryOutputs() {
  const settings = parseBaselineSettings({ LT_BASELINE_TICKET: 'seat', LT_MEASURE_TAG: 'before' });
  const data = { metrics: baselineMetrics({ created: 49, non201: 1, rateLimited: 1 }), state: { testRunDurationMs: 1000 }, setup_data: { eventId: 'e1' } };
  const outputs = buildBaselineSummaryOutputs(settings, false, data);
  const written = JSON.parse(outputs[settings.summaryPath]);
  check(outputs, {
    'summary file has p50, p95 and invalid flags': () =>
      written.baseline.p50 === 35.5 && written.baseline.p95 === 72.25 &&
      written.baseline.isValid === false && written.baseline.isRateLimited === true,
    'stdout shows p50/p95 and rate-limited marker': (o) =>
      o.stdout.includes('p50=35.5') && o.stdout.includes('p95=72.25') && o.stdout.includes('被限流，量測無效'),
  });
  const existing = buildBaselineSummaryOutputs(settings, true, data);
  check(existing, {
    'existing summary is not overwritten': (o) => o[settings.summaryPath] === undefined && o.stdout.includes('already exists'),
  });
  const broken = buildBaselineSummaryOutputs(settings, false, { metrics: null, get state() { throw new Error('boom'); } });
  check(broken, {
    'handleSummary error writes failed summary instead of throwing': (o) =>
      JSON.parse(o[settings.summaryPath]).baseline.isValid === false,
  });
}

export default function () {
  testSettings();
  testOptions();
  testAssignment();
  testDistinctSubjects();
  testEvaluation();
  testSummaryOutputs();
}
