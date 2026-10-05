// [LT-REPORT-003] [LT-REPORT-004] 檔案流程測試：走與 aggregate.js 相同的
// loadRunSummaries → aggregateRuns → renderReportTables → handleSummary 寫檔流程，只把讀取目錄換成唯讀 fixture
// （k6-load-test design.md 決策 11）。fixture 放 11 個白名單檔（缺 seat-ticket-release-run3）與 3 個不該被讀的檔案。
import { check } from 'k6';
import { aggregateRuns, loadRunSummaries, renderReportTables } from '../lib/aggregate.js';

const runs = loadRunSummaries('/scripts/tests/fixtures/aggregate');
const groups = aggregateRuns(runs);
const tables = renderReportTables(groups);

// 白名單外的 fixture 都把 P95／P99 設成這個值；表格出現它就代表讀錯檔。
const DECOY_MARKER = '99999.99';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate==1'] },
};

function findGroup(build, scenario) {
  return groups.find((g) => g.build === build && g.scenario === scenario);
}

function sectionOf(title) {
  const start = tables.indexOf(`### ${title}`);
  if (start < 0) return '';
  const next = tables.indexOf('### ', start + 4);
  return next < 0 ? tables.slice(start) : tables.slice(start, next);
}

export default function () {
  check(runs, {
    '[LT-REPORT-004] exactly 11 whitelisted files are read': (r) => r.length === 12 && r.filter((x) => !x.isMissing).length === 11,
    '[LT-REPORT-004] only seat-ticket-release-run3 is recorded as missing': (r) => {
      const missing = r.filter((x) => x.isMissing).map((x) => x.fileName);
      return missing.length === 1 && missing[0] === 'seat-ticket-release-run3-summary.json';
    },
  });
  check(tables, {
    '[LT-REPORT-004] fault / invalid / precheck files are not included': (t) => !t.includes(DECOY_MARKER),
  });

  const debugCount = findGroup('debug', 'count-ticket');
  const debugCountSection = sectionOf('Debug × 數量票（count-ticket）');
  const runTwoRow = debugCountSection.split('\n').find((line) => line.startsWith('| 2 |')) || '';
  check(debugCount, {
    '[LT-REPORT-003] missing p(95) -> group failed at run 2 naming the field': (g) =>
      g.passed === false && g.failures.length === 1 && g.failures[0].run === 2 &&
      g.failures[0].reasons.some((reason) => reason.includes('p(95)')),
    '[LT-REPORT-003] missing p(95) -> P95 cell shows 缺少': () => runTwoRow.split('|').map((c) => c.trim())[2] === '缺少',
    '[LT-REPORT-003] missing p(95) excluded from P95 stats': (g) =>
      g.p95Stats.count === 2 && g.p95Stats.min === 410.5 && g.p95Stats.max === 430.5,
  });

  check(findGroup('debug', 'seat-ticket'), {
    'runVerdict.passed == false -> group failed pointing at run 1': (g) =>
      g.passed === false && g.failures.length === 1 && g.failures[0].run === 1 &&
      g.failures[0].reasons.some((reason) => reason.includes('place_order_unexpected')),
  });
  check(findGroup('release', 'seat-ticket'), {
    'missing file -> group failed marking run 3 as 缺檔': (g) =>
      g.passed === false && g.failures.length === 1 && g.failures[0].run === 3 && g.failures[0].reasons.includes('缺檔'),
  });
  check(findGroup('release', 'count-ticket'), {
    'complete group -> 通過': (g) => g.passed === true && g.p95Stats.median === 220.5,
  });
  check(sectionOf('Release × 數量票（count-ticket）'), {
    'complete group renders 判定：通過': (s) => s.includes('- 判定：通過'),
  });
}

// 自訂 handleSummary 會取代預設摘要，所以自行輸出 checks 結果，失敗時才看得到是哪一項。
export function handleSummary(data) {
  const checks = data.metrics.checks ? data.metrics.checks.values : {};
  const failedChecks = (data.root_group.checks || []).filter((c) => c.fails > 0).map((c) => `  ✗ ${c.name}`);
  const report = [`checks passes=${checks.passes} fails=${checks.fails}`].concat(failedChecks).join('\n');
  return { '/output/aggregate-flow-report-tables.md': tables, stdout: `\n${tables}\n${report}\n` };
}
