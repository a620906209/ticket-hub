// 無競爭基準：1 個 VU 依序送出 50 筆下單，每筆不同買家、座位票每筆不同座位，全部都應為 201
// （order-placement-p95-optimization design.md 決策 3）。用來取得「單筆在鎖內的成本」，與 50 筆搶鎖的耗時比較。
import exec from 'k6/execution';
import { fail } from 'k6';
import {
  BASELINE_ORDER_COUNT,
  assignBaselineOrder,
  buildBaselineSummaryOutputs,
  createBaselineOptions,
  parseBaselineSettings,
  validateDistinctSubjects,
} from './lib/baseline.js';
import { authHeaders, postJson, withTags } from './lib/http.js';
import { detectExistingSummary, runSetup } from './lib/load-test.js';
import {
  baselineNon201,
  baselineOrdersCreated,
  baselineRateLimited,
  initializeBaselineCounters,
  recordPlaceOrderResponse,
} from './lib/metrics.js';
import { buyerTokens } from './lib/tokens.js';

const settings = parseBaselineSettings(__ENV);
const isSummaryExisting = detectExistingSummary(settings);

export const options = createBaselineOptions();

export function setup() {
  // 設定錯誤與分區唯一性都在建立活動之前檢查：任一不符就中止，不送出任何下單（LT-MEASURE-009、014）。
  if (settings.errors.length > 0) fail(`setup aborted: ${settings.errors.join('; ')}`);
  const subjectError = validateDistinctSubjects(buyerTokens, BASELINE_ORDER_COUNT);
  if (subjectError !== null) fail(`setup aborted: ${subjectError}`);
  initializeBaselineCounters();
  return runSetup(settings, isSummaryExisting);
}

export function placeOrder(data) {
  const { buyerTokenIndex, selection } = assignBaselineOrder(settings.scenario, exec.scenario.iterationInTest, data);
  const response = postJson('/api/orders', { selections: [selection] },
    withTags(authHeaders(buyerTokens[buyerTokenIndex]), 'place-order'));
  recordPlaceOrderResponse(response.status);

  if (response.status === 201) {
    baselineOrdersCreated.add(1);
    return;
  }
  baselineNon201.add(1);
  if (response.status === 429) baselineRateLimited.add(1);
}

export function handleSummary(data) {
  return buildBaselineSummaryOutputs(settings, isSummaryExisting, data);
}
