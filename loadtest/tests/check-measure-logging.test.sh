#!/usr/bin/env bash
# check-measure-logging.sh 的自我測試（order-placement-p95-optimization LT-MEASURE-012）。
# 以假 summary 與假 Seq／Console 指令驗證判定，不連任何開發用服務。執行：bash loadtest/tests/check-measure-logging.test.sh
set -u

SCRIPT="$(cd "$(dirname "$0")/.." && pwd)/check-measure-logging.sh"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
FAILURES=0

# 參數：requests rate_limited no_response
write_summary() {
  cat > "$WORK_DIR/summary.json" <<JSON
{ "metrics": {
  "place_order_requests": { "values": { "count": $1 } },
  "place_order_rate_limited": { "values": { "count": $2 } },
  "place_order_no_response": { "values": { "count": $3 } }
} }
JSON
}

# Seq 筆數依呼叫次數變化：參數為逗號分隔的序列，用完後停在最後一個值（模擬非同步批次寫入逐步補齊）。
write_seq_fake() {
  echo 0 > "$WORK_DIR/seq-calls"
  cat > "$WORK_DIR/seq-count.sh" <<SH
#!/usr/bin/env bash
IFS=',' read -r -a counts <<< "$1"
calls=\$(cat "$WORK_DIR/seq-calls"); echo \$((calls + 1)) > "$WORK_DIR/seq-calls"
index=\$(( calls < \${#counts[@]} ? calls : \${#counts[@]} - 1 ))
echo "\${counts[\$index]}"
SH
}

write_console_fake() {
  printf '%s\n' "$1" > "$WORK_DIR/console.txt"
  printf '#!/usr/bin/env bash\ncat "%s"\n' "$WORK_DIR/console.txt" > "$WORK_DIR/console.sh"
}

run_check() {
  LT_SEQ_COUNT_CMD="bash $WORK_DIR/seq-count.sh" \
  LT_CONSOLE_LOG_CMD="bash $WORK_DIR/console.sh" \
  LT_POLL_TIMEOUT_SECONDS=2 LT_POLL_INTERVAL_SECONDS=0.2 \
    bash "$SCRIPT" 2026-10-06T00:00:00Z 2026-10-06T00:01:30Z "$WORK_DIR/summary.json" > "$WORK_DIR/out.txt" 2>&1
}

expect() {
  local name="$1" expected_exit="$2" expected_text="$3"
  run_check
  local actual_exit=$?
  if [ "$actual_exit" -ne "$expected_exit" ] || ! grep -q -- "$expected_text" "$WORK_DIR/out.txt"; then
    echo "FAIL: $name (exit=$actual_exit, expected=$expected_exit, text='$expected_text')"
    sed 's/^/    /' "$WORK_DIR/out.txt"
    FAILURES=$((FAILURES + 1))
  else
    echo "ok: $name"
  fi
}

CLEAN_CONSOLE='[10:00:00 INF] HTTP POST /api/orders responded 201'
PHASE_CONSOLE='[10:00:00 DBG] PlaceOrder phase timings: Outcome=Success HasSeatItems=True'

write_summary 500 0 0; write_seq_fake 500; write_console_fake "$CLEAN_CONSOLE"
expect "全部符合" 0 "valid: seq=500 expected=500"

write_summary 500 0 0; write_seq_fake 0; write_console_fake "$CLEAN_CONSOLE"
expect "Seq 0 筆（Override 沒生效）" 1 "seq=0 expected=500"

write_summary 500 0 0; write_seq_fake 499; write_console_fake "$CLEAN_CONSOLE"
expect "Seq 少於預期" 1 "seq=499 expected=500"

write_summary 500 0 0; write_seq_fake 501; write_console_fake "$CLEAN_CONSOLE"
expect "Seq 多於預期" 1 "seq=501 expected=500"

write_summary 500 0 0; write_seq_fake 100,300,500; write_console_fake "$CLEAN_CONSOLE"
expect "輪詢期間才補齊" 0 "valid: seq=500 expected=500"

write_summary 500 20 0; write_seq_fake 480; write_console_fake "$CLEAN_CONSOLE"
expect "429 從預期數扣除" 0 "valid: seq=480 expected=480"

write_summary 500 0 3; write_seq_fake 497; write_console_fake "$CLEAN_CONSOLE"
expect "有逾時即無效" 1 "no-response"

write_summary 500 0 0; write_seq_fake 500; write_console_fake "$PHASE_CONSOLE"
expect "Console 出現分段 log" 1 "console"

write_summary 500 0 0; write_seq_fake 500; write_console_fake "$CLEAN_CONSOLE"
echo '{ "metrics": {} }' > "$WORK_DIR/summary.json"
expect "summary 缺少計數器" 1 "missing"

if [ "$FAILURES" -gt 0 ]; then echo "$FAILURES test(s) failed"; exit 1; fi
echo "all tests passed"
