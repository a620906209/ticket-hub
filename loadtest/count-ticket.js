// 數量票 scenario：500 名買家同時搶 50 張純計數票（k6-load-test design.md 決策 5–7）。
import { parseRunSettings } from './lib/config.js';
import {
  buildSummaryOutputs,
  createOptions,
  detectExistingSummary,
  runPlaceOrder,
  runSetup,
  runVerify,
} from './lib/load-test.js';

const settings = parseRunSettings('count-ticket', __ENV);
const isSummaryExisting = detectExistingSummary(settings);

export const options = createOptions(settings);

export function setup() {
  return runSetup(settings, isSummaryExisting);
}

export function placeOrder(data) {
  runPlaceOrder(settings, data, () => ({ ticketTypeId: data.targetTicketTypeId, quantity: 1 }));
}

export function verify(data) {
  runVerify(settings, data);
}

export function handleSummary(data) {
  return buildSummaryOutputs(settings, isSummaryExisting, data);
}
