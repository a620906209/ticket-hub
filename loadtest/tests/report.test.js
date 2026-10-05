// [LT-REPORT-001] [LT-REPORT-002] [LT-REPORT-003] [LT-REPEAT-001] [LT-REPEAT-002]
// evaluateRunResult 與 aggregateRuns／renderReportTables 的自我測試（k6-load-test design.md 決策 9、11）。
// 假資料的結構依真實 summary JSON（handleSummary 的 data.metrics）手寫；與真實結構是否一致由 tasks 6.1 確認。
import { check } from 'k6';
import { evaluateRunResult } from '../lib/result.js';
import { aggregateRuns, renderReportTables } from '../lib/aggregate.js';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate==1'] },
};

function counter(count, thresholds) {
  const metric = { type: 'counter', contains: 'default', values: { count, rate: 1 } };
  if (thresholds) metric.thresholds = thresholds;
  return metric;
}

function passingMetrics({ p95 = 120.5, p99 = 180.25, ordersCreated = 50, quantitySold = 50, conflicts = 450, sendMin = 5, sendMax = 35 } = {}) {
  return {
    'http_req_duration{name:place-order}': {
      type: 'trend',
      contains: 'time',
      values: { avg: 90, min: 10, med: 80, max: 200, 'p(90)': 110, 'p(95)': p95, 'p(99)': p99 },
      thresholds: { 'p(95)<500': { ok: true } },
    },
    orders_created: counter(ordersCreated, { 'count<=50': { ok: true }, 'count>=50': { ok: true } }),
    place_order_conflict: counter(conflicts),
    place_order_5xx: counter(0, { 'count==0': { ok: true } }),
    place_order_unexpected: counter(0, { 'count==0': { ok: true } }),
    confirm_failed: counter(0, { 'count==0': { ok: true } }),
    buyer_iterations_completed: counter(500, { 'count==500': { ok: true } }),
    oversell_check_failed: counter(0, { 'count==0': { ok: true } }),
    verify_quantity_sold: { type: 'gauge', contains: 'default', values: { value: quantitySold, min: quantitySold, max: quantitySold } },
    place_order_send_offset_ms: { type: 'trend', contains: 'default', values: { min: sendMin, max: sendMax, 'p(95)': 30, 'p(99)': 34 } },
  };
}

function summaryOf(metrics) {
  return { metrics, runVerdict: evaluateRunResult({ metrics }) };
}

function hasReason(result, fragment) {
  return result.reasons.some((reason) => reason.includes(fragment));
}

function runEntry(scenario, build, run, summary) {
  return { scenario, build, run, fileName: `${scenario}-${build}-run${run}-summary.json`, isMissing: summary === null, summary };
}

function findGroup(groups, build, scenario) {
  return groups.find((g) => g.build === build && g.scenario === scenario);
}

function testEvaluateRunResult() {
  check(evaluateRunResult({ metrics: passingMetrics() }), {
    'evaluateRunResult: consistent and all thresholds ok -> passed': (r) => r.passed === true && r.reasons.length === 0,
  });

  check(evaluateRunResult({ metrics: passingMetrics({ ordersCreated: 50, quantitySold: 49 }) }), {
    '[LT-REPORT-002] QuantitySold != orders_created -> failed with both numbers': (r) =>
      r.passed === false && hasReason(r, 'QuantitySold (49) != orders_created (50)'),
  });

  const failedThreshold = passingMetrics();
  failedThreshold.place_order_5xx.thresholds['count==0'].ok = false;
  check(evaluateRunResult({ metrics: failedThreshold }), {
    'threshold ok == false -> failed naming the threshold': (r) =>
      r.passed === false && hasReason(r, 'threshold failed: place_order_5xx: count==0'),
  });

  const noGauge = passingMetrics();
  delete noGauge.verify_quantity_sold;
  check(evaluateRunResult({ metrics: noGauge }), {
    '[LT-REPORT-003] missing verify_quantity_sold -> failed': (r) =>
      r.passed === false && hasReason(r, 'metrics["verify_quantity_sold"].values["value"]'),
  });

  const noP95 = passingMetrics();
  delete noP95['http_req_duration{name:place-order}'].values['p(95)'];
  check(evaluateRunResult({ metrics: noP95 }), {
    '[LT-REPORT-003] missing p(95) -> failed naming the field': (r) =>
      r.passed === false && hasReason(r, 'metrics["http_req_duration{name:place-order}"].values["p(95)"]'),
  });

  const noOrders = passingMetrics();
  delete noOrders.orders_created;
  check(evaluateRunResult({ metrics: noOrders }), {
    '[LT-REPORT-003] missing orders_created -> failed naming the field': (r) =>
      r.passed === false && hasReason(r, 'metrics["orders_created"].values["count"]'),
  });

  const no5xx = passingMetrics();
  delete no5xx.place_order_5xx;
  check(evaluateRunResult({ metrics: no5xx }), {
    '[LT-REPORT-003] missing place_order_5xx -> failed (not treated as 0)': (r) =>
      r.passed === false && hasReason(r, 'metrics["place_order_5xx"].values["count"]'),
  });

  const noOk = passingMetrics();
  delete noOk.confirm_failed.thresholds['count==0'].ok;
  check(evaluateRunResult({ metrics: noOk }), {
    '[LT-REPORT-003] threshold without ok -> failed naming the threshold': (r) =>
      r.passed === false && hasReason(r, 'missing field: threshold "confirm_failed: count==0" ok'),
  });

  // 決策 7：送出不集中（最早與最晚相差超過 1 秒）就不是 500 併發，該次不得算通過。
  check(evaluateRunResult({ metrics: passingMetrics({ sendMin: 0, sendMax: 1001 }) }), {
    'send offset spread > 1000ms -> failed naming the spread': (r) =>
      r.passed === false && hasReason(r, 'send offset spread 1001ms > 1000ms'),
  });
  // WSL2 牆上時鐘倒退會讓全部時距一起變負，但請求仍集中送出，不得因此判失敗。
  check(evaluateRunResult({ metrics: passingMetrics({ sendMin: -1600, sendMax: -1500 }) }), {
    'uniformly negative offsets with 100ms spread -> passed': (r) => r.passed === true,
  });
  const noSendMin = passingMetrics();
  delete noSendMin.place_order_send_offset_ms.values.min;
  check(evaluateRunResult({ metrics: noSendMin }), {
    '[LT-REPORT-003] missing send offset min -> failed': (r) =>
      r.passed === false && hasReason(r, 'metrics["place_order_send_offset_ms"].values["min"]'),
  });
}

function testAggregateRuns() {
  // 3 次全過：中位數／最小值／最大值依輸入計算。
  const allPass = aggregateRuns([
    runEntry('count-ticket', 'release', 1, summaryOf(passingMetrics({ p95: 300, p99: 400 }))),
    runEntry('count-ticket', 'release', 2, summaryOf(passingMetrics({ p95: 100, p99: 450 }))),
    runEntry('count-ticket', 'release', 3, summaryOf(passingMetrics({ p95: 200, p99: 420 }))),
  ]);
  const passGroup = findGroup(allPass, 'release', 'count-ticket');
  check(passGroup, {
    '[LT-REPEAT-001] 3 passing runs -> group passed': (g) => g.passed === true && g.failures.length === 0,
    '[LT-REPEAT-001] P95 median/min/max computed from inputs': (g) =>
      g.p95Stats.median === 200 && g.p95Stats.min === 100 && g.p95Stats.max === 300,
    '[LT-REPEAT-001] P99 median/min/max computed from inputs': (g) =>
      g.p99Stats.median === 420 && g.p99Stats.min === 400 && g.p99Stats.max === 450,
  });

  // 第 2 次失敗：該組未通過並指出第 2 次與原因。
  const secondFails = aggregateRuns([
    runEntry('seat-ticket', 'release', 1, summaryOf(passingMetrics())),
    runEntry('seat-ticket', 'release', 2, summaryOf(passingMetrics({ ordersCreated: 50, quantitySold: 48 }))),
    runEntry('seat-ticket', 'release', 3, summaryOf(passingMetrics())),
  ]);
  const failGroup = findGroup(secondFails, 'release', 'seat-ticket');
  check(failGroup, {
    '[LT-REPEAT-002] second run failed -> group failed pointing at run 2 and its reason': (g) =>
      g.passed === false && g.failures.length === 1 && g.failures[0].run === 2 &&
      g.failures[0].reasons.some((reason) => reason.includes('QuantitySold (48) != orders_created (50)')),
  });

  // 缺 1 個檔：該組未通過。
  const missingFile = aggregateRuns([
    runEntry('count-ticket', 'debug', 1, summaryOf(passingMetrics())),
    runEntry('count-ticket', 'debug', 2, null),
    runEntry('count-ticket', 'debug', 3, summaryOf(passingMetrics())),
  ]);
  check(findGroup(missingFile, 'debug', 'count-ticket'), {
    'missing summary file -> group failed with missing-file reason': (g) =>
      g.passed === false && g.failures.length === 1 && g.failures[0].run === 2 && g.failures[0].reasons.includes('缺檔'),
  });

  // 某次缺 p(99)、QuantitySold、成功數或 409 數：顯示「缺少」、不參與統計、該組未通過。
  const fieldCases = [
    { name: 'p(99)', mutate: (m) => delete m['http_req_duration{name:place-order}'].values['p(99)'], key: 'p99' },
    { name: 'QuantitySold', mutate: (m) => delete m.verify_quantity_sold, key: 'quantitySold' },
    { name: 'orders_created', mutate: (m) => delete m.orders_created, key: 'ordersCreated' },
    { name: '409 count', mutate: (m) => delete m.place_order_conflict, key: 'conflicts' },
  ];
  for (const fieldCase of fieldCases) {
    const metrics = passingMetrics({ p99: 999 });
    // runVerdict 依舊標 passed，確認彙整端自己也會發現缺欄位，而不是只相信 runVerdict。
    const summary = { metrics, runVerdict: { passed: true, reasons: [] } };
    fieldCase.mutate(metrics);
    const groups = aggregateRuns([
      runEntry('seat-ticket', 'debug', 1, summaryOf(passingMetrics({ p99: 100 }))),
      runEntry('seat-ticket', 'debug', 2, summary),
      runEntry('seat-ticket', 'debug', 3, summaryOf(passingMetrics({ p99: 300 }))),
    ]);
    const group = findGroup(groups, 'debug', 'seat-ticket');
    const table = renderReportTables([group]);
    const runTwoRow = table.split('\n').find((line) => line.startsWith('| 2 |'));
    check(group, {
      [`[LT-REPORT-003] missing ${fieldCase.name} -> value null and group failed at run 2`]: (g) =>
        g.runs[1].values[fieldCase.key] === null && g.passed === false && g.failures[0].run === 2,
      [`[LT-REPORT-003] missing ${fieldCase.name} -> table cell shows 缺少`]: () =>
        runTwoRow !== undefined && runTwoRow.includes('缺少'),
    });
    if (fieldCase.key === 'p99') {
      check(group, {
        '[LT-REPORT-003] missing p(99) is excluded from stats (not counted as 0)': (g) =>
          g.p99Stats.count === 2 && g.p99Stats.min === 100 && g.p99Stats.max === 300 && g.p99Stats.median === 200,
      });
    }
  }
}

function testSendSpreadInAggregate() {
  // 較早產生的 summary 的 runVerdict 不含送出時距判定：彙整端必須自己重算，不能只信 runVerdict。
  const legacyMetrics = passingMetrics({ sendMin: 0, sendMax: 1200 });
  const legacy = { metrics: legacyMetrics, runVerdict: { passed: true, reasons: [] } };
  const legacyGroup = findGroup(aggregateRuns([
    runEntry('count-ticket', 'debug', 1, summaryOf(passingMetrics())),
    runEntry('count-ticket', 'debug', 2, legacy),
    runEntry('count-ticket', 'debug', 3, summaryOf(passingMetrics())),
  ]), 'debug', 'count-ticket');
  const runTwoRow = renderReportTables([legacyGroup]).split('\n').find((line) => line.startsWith('| 2 |'));
  check(legacyGroup, {
    'legacy passed verdict but spread 1200ms -> group failed at run 2': (g) =>
      g.passed === false && g.failures.length === 1 && g.failures[0].run === 2 &&
      g.failures[0].reasons.some((reason) => reason.includes('send offset spread 1200ms')),
    'spread column shows max-min': () => runTwoRow !== undefined && runTwoRow.split('|').map((c) => c.trim())[8] === '1200.00',
  });

  // 新 summary 的 runVerdict 已含同一原因：不重複列出。
  const current = summaryOf(passingMetrics({ sendMin: 0, sendMax: 1200 }));
  const currentGroup = findGroup(aggregateRuns([
    runEntry('count-ticket', 'debug', 1, current),
    runEntry('count-ticket', 'debug', 2, summaryOf(passingMetrics())),
    runEntry('count-ticket', 'debug', 3, summaryOf(passingMetrics())),
  ]), 'debug', 'count-ticket');
  check(currentGroup, {
    'spread reason already in runVerdict -> listed once': (g) =>
      g.failures[0].reasons.join('\n').split('send offset spread').length - 1 === 1,
  });
}

function testRenderReportTables() {
  const groups = aggregateRuns([
    runEntry('count-ticket', 'release', 1, summaryOf(passingMetrics({ p95: 123.45, p99: 234.56, ordersCreated: 50, quantitySold: 50, conflicts: 450 }))),
    runEntry('count-ticket', 'release', 2, summaryOf(passingMetrics({ p95: 111.11, p99: 222.22 }))),
    runEntry('count-ticket', 'release', 3, summaryOf(passingMetrics({ p95: 133.33, p99: 244.44 }))),
  ]);
  const table = renderReportTables([findGroup(groups, 'release', 'count-ticket')]);
  const row = table.split('\n').find((line) => line.startsWith('| 1 |'));
  const cells = row ? row.split('|').map((cell) => cell.trim()) : [];
  check(table, {
    '[LT-REPORT-001] row 1 P95/P99/success/409/QuantitySold equal the input': () =>
      cells[2] === '123.45' && cells[3] === '234.56' && cells[4] === '50' && cells[5] === '450' && cells[7] === '50',
    '[LT-REPORT-001] stats line equals inputs': (t) =>
      t.includes('- P95 (ms)（3 次有值）：中位數 123.45／最小值 111.11／最大值 133.33'),
    'group verdict rendered as 通過': (t) => t.includes('- 判定：通過'),
  });
}

export default function () {
  testEvaluateRunResult();
  testAggregateRuns();
  testSendSpreadInAggregate();
  testRenderReportTables();
}
