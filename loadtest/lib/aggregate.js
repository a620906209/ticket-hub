// 報告資料表由 summary JSON 產生（k6-load-test design.md 決策 11）。讀不到預期欄位一律顯示「缺少」、
// 該次判未通過、不參與統計，不以 0 代替。
import { PLACE_ORDER_DURATION_METRIC, evaluateSendSpread, readMetricValue } from './result.js';

export const SCENARIOS = ['count-ticket', 'seat-ticket'];
export const API_BUILDS = ['release', 'debug'];
export const RUNS = [1, 2, 3];
export const MISSING_CELL = '缺少';
export const MISSING_FILE_CELL = '缺檔';

const SCENARIO_LABELS = { 'count-ticket': '數量票（count-ticket）', 'seat-ticket': '座位票（seat-ticket）' };
const BUILD_LABELS = { release: 'Release', debug: 'Debug' };

// 每次執行在表格中呈現的欄位；`format` 決定小數位數。
const RUN_FIELDS = [
  { key: 'p95', metric: PLACE_ORDER_DURATION_METRIC, field: 'p(95)', format: 'ms' },
  { key: 'p99', metric: PLACE_ORDER_DURATION_METRIC, field: 'p(99)', format: 'ms' },
  { key: 'ordersCreated', metric: 'orders_created', field: 'count', format: 'int' },
  { key: 'conflicts', metric: 'place_order_conflict', field: 'count', format: 'int' },
  { key: 'serverErrors', metric: 'place_order_5xx', field: 'count', format: 'int' },
  { key: 'quantitySold', metric: 'verify_quantity_sold', field: 'value', format: 'int' },
];
// 送出時距是衍生值（max − min），不在 RUN_FIELDS；欄位缺少的處理同上。
const SEND_SPREAD_KEY = 'sendOffsetSpread';

export function summaryFileName(scenario, build, run) {
  return `${scenario}-${build}-run${run}-summary.json`;
}

/** 白名單的 12 個檔名，固定展開、不用 glob，precheck／故障注入／invalid 檔不會被納入。 */
export function whitelistFileNames() {
  const names = [];
  for (const scenario of SCENARIOS)
    for (const build of ['debug', 'release'])
      for (const run of RUNS) names.push({ scenario, build, run, fileName: summaryFileName(scenario, build, run) });
  return names;
}

/**
 * 只能在 init 階段呼叫（open() 限制）。檔案不存在記為缺檔、JSON 無法解析記為 parseError，都不拋例外。
 */
export function loadRunSummaries(baseDir) {
  return whitelistFileNames().map((entry) => {
    let text;
    try {
      text = open(`${baseDir}/${entry.fileName}`);
    } catch (_) {
      return Object.assign({}, entry, { isMissing: true, summary: null });
    }
    try {
      return Object.assign({}, entry, { isMissing: false, summary: JSON.parse(text) });
    } catch (error) {
      return Object.assign({}, entry, { isMissing: false, summary: null, parseError: String(error) });
    }
  });
}

function evaluateRun(entry) {
  if (!entry || entry.isMissing) return { isMissing: true, passed: false, values: {}, reasons: ['缺檔'] };
  if (!entry.summary) return { isMissing: false, passed: false, values: {}, reasons: [`summary JSON 無法解析：${entry.parseError}`] };

  const metrics = entry.summary.metrics;
  const values = {};
  const reasons = [];
  for (const spec of RUN_FIELDS) {
    const value = readMetricValue(metrics, spec.metric, spec.field);
    values[spec.key] = value === undefined ? null : value;
    if (value === undefined) reasons.push(`缺少欄位：metrics["${spec.metric}"].values["${spec.field}"]`);
  }
  // 在此重算而非只信 runVerdict：較早產生的 summary 的 runVerdict 尚未包含這項判定。
  const sendSpread = evaluateSendSpread(metrics);
  values[SEND_SPREAD_KEY] = sendSpread.spread;

  const verdict = entry.summary.runVerdict;
  if (!verdict || typeof verdict.passed !== 'boolean') {
    reasons.push('缺少欄位：runVerdict.passed');
  } else if (!verdict.passed) {
    const verdictReasons = Array.isArray(verdict.reasons) ? verdict.reasons : [];
    reasons.push(verdictReasons.length > 0 ? `runVerdict 未通過：${verdictReasons.join('；')}` : 'runVerdict 未通過（未列原因）');
  }
  const isSpreadReasonInVerdict = verdict && Array.isArray(verdict.reasons) && verdict.reasons.includes(sendSpread.reason);
  if (sendSpread.reason !== null && !isSpreadReasonInVerdict) reasons.push(sendSpread.reason);
  return { isMissing: false, passed: reasons.length === 0, values, reasons };
}

function median(sorted) {
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

/** 缺少的值不參與統計；全部缺少時三個統計值都是 null。 */
export function describeStats(values) {
  const present = values.filter((value) => typeof value === 'number').sort((a, b) => a - b);
  if (present.length === 0) return { median: null, min: null, max: null, count: 0 };
  return { median: median(present), min: present[0], max: present[present.length - 1], count: present.length };
}

/**
 * @param {Array} runs `loadRunSummaries` 的結果（或同結構的假資料）
 * @returns {Array} 每組（組態 × scenario）的 3 次結果、統計與判定
 */
export function aggregateRuns(runs) {
  const groups = [];
  for (const build of API_BUILDS) {
    for (const scenario of SCENARIOS) {
      const runResults = RUNS.map((run) => {
        const entry = (runs || []).find((r) => r && r.scenario === scenario && r.build === build && r.run === run);
        return Object.assign({ run }, evaluateRun(entry));
      });
      const failures = runResults
        .filter((r) => !r.passed)
        .map((r) => ({ run: r.run, reasons: r.reasons }));
      groups.push({
        build,
        scenario,
        runs: runResults,
        p95Stats: describeStats(runResults.map((r) => r.values.p95)),
        p99Stats: describeStats(runResults.map((r) => r.values.p99)),
        passed: failures.length === 0,
        failures,
      });
    }
  }
  return groups;
}

export function formatCell(value, format) {
  if (typeof value !== 'number') return MISSING_CELL;
  return format === 'ms' ? value.toFixed(2) : String(value);
}

function renderStatsLine(label, stats) {
  if (stats.count === 0) return `- ${label}：中位數 ${MISSING_CELL}／最小值 ${MISSING_CELL}／最大值 ${MISSING_CELL}`;
  return `- ${label}（${stats.count} 次有值）：中位數 ${stats.median.toFixed(2)}／最小值 ${stats.min.toFixed(2)}／最大值 ${stats.max.toFixed(2)}`;
}

/** 表格內容會原樣貼進 docs/load-test/report.md，以 diff 比對（LT-REPORT-001）。 */
export function renderReportTables(groups) {
  const lines = [];
  for (const group of groups) {
    lines.push(`### ${BUILD_LABELS[group.build]} × ${SCENARIO_LABELS[group.scenario]}`, '');
    lines.push('| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |');
    lines.push('|---|---|---|---|---|---|---|---|---|');
    for (const run of group.runs) {
      const cells = run.isMissing
        ? RUN_FIELDS.map(() => MISSING_FILE_CELL).concat(MISSING_FILE_CELL)
        : RUN_FIELDS.map((spec) => formatCell(run.values[spec.key], spec.format)).concat(formatCell(run.values[SEND_SPREAD_KEY], 'ms'));
      lines.push(`| ${run.run} | ${cells.join(' | ')} | ${run.passed ? '通過' : '未通過'} |`);
    }
    lines.push('');
    lines.push(renderStatsLine('P95 (ms)', group.p95Stats));
    lines.push(renderStatsLine('P99 (ms)', group.p99Stats));
    if (group.passed) {
      lines.push('- 判定：通過');
    } else {
      lines.push('- 判定：未通過');
      for (const failure of group.failures) lines.push(`  - 第 ${failure.run} 次：${failure.reasons.join('；')}`);
    }
    lines.push('');
  }
  return lines.join('\n');
}
