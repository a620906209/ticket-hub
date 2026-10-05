// 單次執行的最終判定（k6-load-test design.md 決策 7、11）。純函式，輸入為 handleSummary 的 data
// （或從 summary JSON 讀回的同結構物件）。讀不到預期欄位一律判失敗並寫明缺少什麼，不預設為 0。

export const PLACE_ORDER_DURATION_METRIC = 'http_req_duration{name:place-order}';

export const REQUIRED_COUNTERS = [
  'orders_created',
  'place_order_conflict',
  'place_order_5xx',
  'place_order_unexpected',
  'confirm_failed',
  'buyer_iterations_completed',
  'oversell_check_failed',
];

// 兩支壓測腳本都對這些 metric 設 threshold；summary 中沒有 thresholds 代表結構不符預期，判失敗。
export const REQUIRED_THRESHOLD_METRICS = [
  PLACE_ORDER_DURATION_METRIC,
  'orders_created',
  'place_order_5xx',
  'place_order_unexpected',
  'confirm_failed',
  'buyer_iterations_completed',
  'oversell_check_failed',
];

/** 讀 `metrics[name].values[field]`；不是數字時回傳 undefined（不轉成 0）。 */
export function readMetricValue(metrics, name, field) {
  const metric = metrics && metrics[name];
  const value = metric && metric.values ? metric.values[field] : undefined;
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

export function describeMissing(name, field) {
  return `missing field: metrics["${name}"].values["${field}"]`;
}

export const SEND_OFFSET_METRIC = 'place_order_send_offset_ms';
export const SEND_SPREAD_LIMIT_MS = 1000;

/**
 * 500 個請求是否集中送出（design 決策 7）。看最大值減最小值而非最大值：k6 只有牆上時鐘
 * （`Date.now()` 與 `exec.instance.currentTestRunDuration` 皆是），WSL2 時鐘每 ~29 秒倒退約 1.5 秒
 * （2026-10-05 實測），等待期間倒退會讓所有 VU 的時距一起平移，但不改變彼此的差距。
 * @returns {{ spread: number | null, reason: string | null }}
 */
export function evaluateSendSpread(metrics) {
  const min = readMetricValue(metrics, SEND_OFFSET_METRIC, 'min');
  const max = readMetricValue(metrics, SEND_OFFSET_METRIC, 'max');
  if (min === undefined) return { spread: null, reason: describeMissing(SEND_OFFSET_METRIC, 'min') };
  if (max === undefined) return { spread: null, reason: describeMissing(SEND_OFFSET_METRIC, 'max') };
  const spread = max - min;
  return {
    spread,
    reason: spread > SEND_SPREAD_LIMIT_MS ? `send offset spread ${Math.round(spread)}ms > ${SEND_SPREAD_LIMIT_MS}ms (max-min)` : null,
  };
}

/**
 * @returns {{ passed: boolean, reasons: string[] }}
 */
export function evaluateRunResult(data) {
  const reasons = [];
  const metrics = data && typeof data.metrics === 'object' && data.metrics !== null ? data.metrics : null;
  if (metrics === null) return { passed: false, reasons: ['missing field: metrics'] };

  for (const name of REQUIRED_COUNTERS) {
    if (readMetricValue(metrics, name, 'count') === undefined) reasons.push(describeMissing(name, 'count'));
  }
  for (const field of ['p(95)', 'p(99)']) {
    if (readMetricValue(metrics, PLACE_ORDER_DURATION_METRIC, field) === undefined)
      reasons.push(describeMissing(PLACE_ORDER_DURATION_METRIC, field));
  }

  const quantitySold = readMetricValue(metrics, 'verify_quantity_sold', 'value');
  const ordersCreated = readMetricValue(metrics, 'orders_created', 'count');
  if (quantitySold === undefined) reasons.push(describeMissing('verify_quantity_sold', 'value'));
  else if (ordersCreated !== undefined && quantitySold !== ordersCreated)
    reasons.push(`QuantitySold (${quantitySold}) != orders_created (${ordersCreated})`);

  const sendSpread = evaluateSendSpread(metrics);
  if (sendSpread.reason !== null) reasons.push(sendSpread.reason);

  for (const name of REQUIRED_THRESHOLD_METRICS) {
    const thresholds = metrics[name] && metrics[name].thresholds;
    if (!thresholds || typeof thresholds !== 'object' || Object.keys(thresholds).length === 0)
      reasons.push(`missing field: metrics["${name}"].thresholds`);
  }
  for (const name of Object.keys(metrics)) {
    const thresholds = metrics[name] && metrics[name].thresholds;
    if (!thresholds || typeof thresholds !== 'object') continue;
    for (const expression of Object.keys(thresholds)) {
      const ok = thresholds[expression] ? thresholds[expression].ok : undefined;
      if (typeof ok !== 'boolean') reasons.push(`missing field: threshold "${name}: ${expression}" ok`);
      else if (!ok) reasons.push(`threshold failed: ${name}: ${expression}`);
    }
  }

  return { passed: reasons.length === 0, reasons };
}
