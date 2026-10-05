// 伺服器端超賣驗證的判斷邏輯（k6-load-test design.md 決策 7）。純函式、不碰 HTTP，
// 讓 tests/verify.test.js 能以假資料逐條觸發每個失敗條件，不必真的製造超賣。

export const AVAILABLE_SEAT_STATUS = 'Available';

/**
 * @param {object} input
 * @param {'count-ticket'|'seat-ticket'} input.scenario
 * @param {string} input.targetTicketTypeId
 * @param {number} input.ticketLimit 目標張數（50）
 * @param {string} input.hotZone 座位票目標分區（HOT）
 * @param {object} input.salesReport `GET /api/admin/events/{id}/sales-report` 回應
 * @param {Array} input.ticketTypes `GET /api/events/{id}/ticket-types` 回應
 * @param {Array} input.seats `GET /api/events/{id}/seats` 回應
 * @returns {{ quantitySold: number|null, reasons: string[] }}
 */
export function evaluateVerification(input) {
  const reasons = [];
  const salesEntry = input.salesReport && Array.isArray(input.salesReport.byTicketType)
    ? input.salesReport.byTicketType.find((entry) => entry.ticketTypeId === input.targetTicketTypeId)
    : undefined;
  if (!salesEntry || typeof salesEntry.quantitySold !== 'number') {
    reasons.push('sales report has no QuantitySold for the target ticket type');
    return { quantitySold: null, reasons };
  }

  const quantitySold = salesEntry.quantitySold;
  if (quantitySold > input.ticketLimit) reasons.push(`QuantitySold > ${input.ticketLimit} (QuantitySold=${quantitySold})`);

  if (input.scenario === 'count-ticket') {
    if (quantitySold !== input.ticketLimit)
      reasons.push(`count ticket QuantitySold != ${input.ticketLimit} (QuantitySold=${quantitySold})`);
    const ticketType = Array.isArray(input.ticketTypes)
      ? input.ticketTypes.find((t) => t.id === input.targetTicketTypeId)
      : undefined;
    if (!ticketType) reasons.push('target count ticket type not found in ticket type list');
    else if (ticketType.availableQuantity !== 0)
      reasons.push(`count ticket remaining quantity != 0 (availableQuantity=${ticketType.availableQuantity})`);
  } else {
    if (!Array.isArray(input.seats)) {
      reasons.push('seat list is missing');
    } else {
      const unavailable = input.seats.filter((seat) => seat.status !== AVAILABLE_SEAT_STATUS);
      const hotUnavailable = unavailable.filter((seat) => seat.zoneCode === input.hotZone).length;
      const otherUnavailable = unavailable.length - hotUnavailable;
      if (hotUnavailable !== quantitySold)
        reasons.push(`${input.hotZone} unavailable seats != QuantitySold (unavailable=${hotUnavailable}, QuantitySold=${quantitySold})`);
      if (otherUnavailable > 0)
        reasons.push(`non-${input.hotZone} zones have unavailable seats (count=${otherUnavailable})`);
    }
  }

  return { quantitySold, reasons };
}
