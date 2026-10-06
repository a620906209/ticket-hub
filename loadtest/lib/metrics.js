import { Counter, Gauge, Trend } from 'k6/metrics';

export const ordersCreated = new Counter('orders_created');
export const placeOrderConflict = new Counter('place_order_conflict');
export const placeOrder5xx = new Counter('place_order_5xx');
export const placeOrderUnexpected = new Counter('place_order_unexpected');
export const confirmFailed = new Counter('confirm_failed');
export const buyerIterationsCompleted = new Counter('buyer_iterations_completed');
export const oversellCheckFailed = new Counter('oversell_check_failed');
// 只供量測有效性檢查（check-measure-logging.sh）推算伺服器端應有的分段 log 筆數，不參與任何門檻或判定
// （order-placement-p95-optimization design.md 決策 1）。
export const placeOrderRequests = new Counter('place_order_requests');
export const placeOrderRateLimited = new Counter('place_order_rate_limited');
export const placeOrderNoResponse = new Counter('place_order_no_response');

// 無競爭基準專用（order-placement-p95-optimization design.md 決策 3）；不在正式壓測的 counters 清單內。
export const baselineOrdersCreated = new Counter('baseline_orders_created');
export const baselineNon201 = new Counter('baseline_non_201');
export const baselineRateLimited = new Counter('baseline_rate_limited');

// 刻意不補 0：Gauge 沒值代表 verify 沒跑到，必須判為未通過；Trend 補 0 會污染統計（design 決策 11）。
export const verifyQuantitySold = new Gauge('verify_quantity_sold');
export const placeOrderSendOffsetMs = new Trend('place_order_send_offset_ms');

const counters = [
  ordersCreated,
  placeOrderConflict,
  placeOrder5xx,
  placeOrderUnexpected,
  confirmFailed,
  buyerIterationsCompleted,
  oversellCheckFailed,
  placeOrderRequests,
  placeOrderRateLimited,
  placeOrderNoResponse,
];

/** 從未遞增的 Counter 不會出現在 summary；先各加 0，彙整端才能把「沒出現」一律視為缺少。 */
export function initializeCounters() {
  for (const counter of counters) counter.add(0);
}

/** 每筆下單回應呼叫一次；status 0 是 k6 沒收到回應（逾時或網路錯誤）。 */
export function recordPlaceOrderResponse(status) {
  placeOrderRequests.add(1);
  if (status === 429) placeOrderRateLimited.add(1);
  if (status === 0) placeOrderNoResponse.add(1);
}

/** 基準判定把「counter 缺少」視為無效，所以同樣先各加 0。 */
export function initializeBaselineCounters() {
  for (const counter of [baselineOrdersCreated, baselineNon201, baselineRateLimited]) counter.add(0);
}
