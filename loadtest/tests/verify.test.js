// [LT-GATE-004] evaluateVerification 自我測試：以假資料逐條觸發每個失敗條件，不必真的製造超賣
// （k6-load-test design.md 決策 9）。任一 check 失敗時 `checks: rate==1` 讓 exit code 非 0。
import { check } from 'k6';
import { evaluateVerification } from '../lib/verify.js';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate==1'] },
};

const TARGET_ID = 'target-ticket-type';

function salesReport(quantitySold) {
  return { byTicketType: [{ ticketTypeId: 'other', quantitySold: 0 }, { ticketTypeId: TARGET_ID, quantitySold }] };
}

function countInput(quantitySold, availableQuantity) {
  return {
    scenario: 'count-ticket',
    targetTicketTypeId: TARGET_ID,
    ticketLimit: 50,
    hotZone: 'HOT',
    salesReport: salesReport(quantitySold),
    ticketTypes: [{ id: 'z01', availableQuantity: null }, { id: TARGET_ID, availableQuantity }],
    seats: [],
  };
}

function seats(hotSold, otherSold) {
  const list = [];
  // 超賣情境需要比 50 席多的非可售 HOT 座位，才能只觸發「QuantitySold > 50」而不連帶觸發席數不符。
  for (let i = 0; i < Math.max(50, hotSold); i++) list.push({ zoneCode: 'HOT', status: i < hotSold ? 'Sold' : 'Available' });
  for (let i = 0; i < 100; i++) list.push({ zoneCode: 'Z01', status: i < otherSold ? 'Held' : 'Available' });
  return list;
}

function seatInput(quantitySold, hotSold, otherSold) {
  return {
    scenario: 'seat-ticket',
    targetTicketTypeId: TARGET_ID,
    ticketLimit: 50,
    hotZone: 'HOT',
    salesReport: salesReport(quantitySold),
    ticketTypes: [],
    seats: seats(hotSold, otherSold),
  };
}

function hasReason(result, fragment) {
  return result.reasons.some((reason) => reason.includes(fragment));
}

export default function () {
  // 正常資料：數量票恰好 50 張、剩餘 0；座位票 HOT 非可售席數等於 QuantitySold、其他分區全可售。
  const countOk = evaluateVerification(countInput(50, 0));
  check(countOk, {
    'count ticket: consistent data returns no reasons': (r) => r.reasons.length === 0 && r.quantitySold === 50,
  });
  const seatOk = evaluateVerification(seatInput(48, 48, 0));
  check(seatOk, {
    'seat ticket: consistent data returns no reasons': (r) => r.reasons.length === 0 && r.quantitySold === 48,
  });

  // 每個失敗條件各自觸發；超賣判斷若被移除，對應的 check 會失敗。
  check(evaluateVerification(seatInput(51, 51, 0)), {
    'QuantitySold > 50 is reported': (r) => hasReason(r, 'QuantitySold > 50') && r.reasons.length === 1,
  });
  check(evaluateVerification(countInput(49, 0)), {
    'count ticket QuantitySold != 50 is reported': (r) => hasReason(r, 'count ticket QuantitySold != 50') && r.reasons.length === 1,
  });
  check(evaluateVerification(countInput(50, 1)), {
    'count ticket remaining quantity != 0 is reported': (r) => hasReason(r, 'remaining quantity != 0') && r.reasons.length === 1,
  });
  check(evaluateVerification(seatInput(48, 49, 0)), {
    'seat ticket HOT unavailable seats != QuantitySold is reported': (r) => hasReason(r, 'HOT unavailable seats != QuantitySold') && r.reasons.length === 1,
  });
  check(evaluateVerification(seatInput(48, 48, 1)), {
    'seat ticket non-HOT unavailable seat is reported': (r) => hasReason(r, 'non-HOT zones have unavailable seats') && r.reasons.length === 1,
  });

  // 取不到 QuantitySold 不得視為 0（design 決策 7）。
  const missing = countInput(50, 0);
  missing.salesReport = { byTicketType: [{ ticketTypeId: 'other', quantitySold: 0 }] };
  check(evaluateVerification(missing), {
    'missing target in sales report is reported with null QuantitySold': (r) => r.quantitySold === null && hasReason(r, 'no QuantitySold'),
  });
}
