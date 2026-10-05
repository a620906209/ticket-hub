import { Counter, Gauge, Trend } from 'k6/metrics';

export const ordersCreated = new Counter('orders_created');
export const placeOrderConflict = new Counter('place_order_conflict');
export const placeOrder5xx = new Counter('place_order_5xx');
export const placeOrderUnexpected = new Counter('place_order_unexpected');
export const confirmFailed = new Counter('confirm_failed');
export const buyerIterationsCompleted = new Counter('buyer_iterations_completed');
export const oversellCheckFailed = new Counter('oversell_check_failed');

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
];

/** 從未遞增的 Counter 不會出現在 summary；先各加 0，彙整端才能把「沒出現」一律視為缺少。 */
export function initializeCounters() {
  for (const counter of counters) counter.add(0);
}
