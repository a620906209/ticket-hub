#!/usr/bin/env bash
# 量測執行的分段 log 有效性檢查（order-placement-p95-optimization design.md 決策 1，LT-MEASURE-012）。
# 每次開啟分段 log 的量測執行後必跑；非 0 結束代表該次量測無效，不得寫入報告。
#
# 用法：bash loadtest/check-measure-logging.sh <起始時間 UTC ISO8601> <結束時間 UTC ISO8601> <k6 summary JSON>
#
# 預期筆數 = summary 中有收到回應且非 429 的下單請求數：429 在 rate limiter 被擋下、沒有進入 PlaceOrderAsync；
# k6 沒收到回應（status 0，含逾時）無法判斷伺服器是否處理完，只要有就直接判無效，不推算筆數。
# 時間範圍只用來篩選 log，不拿來相減算耗時（WSL2 牆上時鐘會倒退）。
#
# 測試用覆寫（tests/check-measure-logging.test.sh）：LT_SEQ_COUNT_CMD、LT_CONSOLE_LOG_CMD、
# LT_POLL_TIMEOUT_SECONDS（預設 15）、LT_POLL_INTERVAL_SECONDS（預設 1）。
set -euo pipefail

PHASE_LOG_TEXT='PlaceOrder phase timings:'

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <start-utc> <end-utc> <summary-json>" >&2
  exit 2
fi
START_UTC="$1"
END_UTC="$2"
SUMMARY_PATH="$3"
POLL_TIMEOUT_SECONDS="${LT_POLL_TIMEOUT_SECONDS:-15}"
POLL_INTERVAL_SECONDS="${LT_POLL_INTERVAL_SECONDS:-1}"
SEQ_URL="${LT_SEQ_URL:-http://localhost:${SEQ_HOST_PORT:-8081}}"

count_seq_phase_logs() {
  if [ -n "${LT_SEQ_COUNT_CMD:-}" ]; then
    $LT_SEQ_COUNT_CMD
    return
  fi
  curl -fsS -G "$SEQ_URL/api/data" \
    --data-urlencode "q=select count(*) from stream where @MessageTemplate like '${PHASE_LOG_TEXT}%'" \
    --data-urlencode "rangeStartUtc=$START_UTC" \
    --data-urlencode "rangeEndUtc=$END_UTC" | jq -r '.Rows[0][0] // 0'
}

read_api_console_logs() {
  if [ -n "${LT_CONSOLE_LOG_CMD:-}" ]; then
    $LT_CONSOLE_LOG_CMD
    return
  fi
  docker compose logs --no-color --since "$START_UTC" api
}

read_counter() {
  jq -r --arg name "$1" '.metrics[$name].values.count // empty' "$SUMMARY_PATH"
}

REQUESTS="$(read_counter place_order_requests)"
RATE_LIMITED="$(read_counter place_order_rate_limited)"
NO_RESPONSE="$(read_counter place_order_no_response)"
if [ -z "$REQUESTS" ] || [ -z "$RATE_LIMITED" ] || [ -z "$NO_RESPONSE" ]; then
  echo "INVALID: summary is missing place_order_requests / place_order_rate_limited / place_order_no_response counters" >&2
  exit 1
fi
if [ "$NO_RESPONSE" -gt 0 ]; then
  echo "INVALID: $NO_RESPONSE place-order request(s) got no-response (timeout or network error); server-side count is unknowable" >&2
  exit 1
fi
EXPECTED=$((REQUESTS - RATE_LIMITED))

CONSOLE_PHASE_LINES="$(read_api_console_logs | grep -cF "$PHASE_LOG_TEXT" || true)"
if [ "$CONSOLE_PHASE_LINES" -ne 0 ]; then
  echo "INVALID: api console contains $CONSOLE_PHASE_LINES phase timing log line(s); Console sink restriction is not in effect" >&2
  exit 1
fi

# Seq 是非同步批次寫入：輪詢到筆數達到預期或等滿上限才判定，避免尚未 flush 就誤判；超過預期同樣無效。
DEADLINE=$((SECONDS + POLL_TIMEOUT_SECONDS))
while true; do
  SEQ_COUNT="$(count_seq_phase_logs)"
  if [ "$SEQ_COUNT" -ge "$EXPECTED" ] || [ "$SECONDS" -ge "$DEADLINE" ]; then
    break
  fi
  sleep "$POLL_INTERVAL_SECONDS"
done

if [ "$SEQ_COUNT" -ne "$EXPECTED" ]; then
  echo "INVALID: seq=$SEQ_COUNT expected=$EXPECTED (requests=$REQUESTS rate-limited=$RATE_LIMITED)" >&2
  exit 1
fi
echo "valid: seq=$SEQ_COUNT expected=$EXPECTED (requests=$REQUESTS rate-limited=$RATE_LIMITED)"
