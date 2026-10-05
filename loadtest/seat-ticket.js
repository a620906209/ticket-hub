// 座位票 scenario：500 名買家同時從 HOT 分區 50 席中隨機搶 1 席（k6-load-test design.md 決策 5–7）。
import { parseRunSettings } from './lib/config.js';
import {
  buildSummaryOutputs,
  createOptions,
  detectExistingSummary,
  runPlaceOrder,
  runSetup,
  runVerify,
} from './lib/load-test.js';

const settings = parseRunSettings('seat-ticket', __ENV);
const isSummaryExisting = detectExistingSummary(settings);

export const options = createOptions(settings);

export function setup() {
  return runSetup(settings, isSummaryExisting);
}

export function placeOrder(data) {
  // 隨機選位，以真實撞位為目標（design 決策 6）。
  runPlaceOrder(settings, data, () => ({
    eventSeatId: data.seatPool[Math.floor(Math.random() * data.seatPool.length)],
    ticketTypeId: data.targetTicketTypeId,
    quantity: 1,
  }));
}

export function verify(data) {
  runVerify(settings, data);
}

export function handleSummary(data) {
  return buildSummaryOutputs(settings, isSummaryExisting, data);
}
