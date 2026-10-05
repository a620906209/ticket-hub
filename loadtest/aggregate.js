// 彙整 12 份正式 summary JSON，產生報告資料表 /output/report-tables.md（k6-load-test design.md 決策 11）。
// 只讀 /output 下白名單檔名；不發 HTTP 請求、不讀 token 檔、不讀 __ENV。
import { aggregateRuns, loadRunSummaries, renderReportTables } from './lib/aggregate.js';

const runs = loadRunSummaries('/output');

export const options = { vus: 1, iterations: 1 };

export default function () {}

export function handleSummary() {
  try {
    const tables = renderReportTables(aggregateRuns(runs));
    return { '/output/report-tables.md': tables, stdout: `\n${tables}\n` };
  } catch (error) {
    return { stdout: `\naggregate error: ${String(error)}\n` };
  }
}
