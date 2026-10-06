#!/usr/bin/env bash
# [LT-REPORT-005] [LT-REPORT-006] check-report-tables.sh 自我測試（order-placement-p95-optimization design.md 決策 7）。
# 報告資料表是驗收證據：手改一個數字、或未達標卻沒引用量測證據，都必須被擋下。全部用假資料，不讀 .output。
#
# 用法：bash loadtest/tests/check-report-tables.test.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CHECKER="$SCRIPT_DIR/../check-report-tables.sh"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
FAILURES=0

# 依 aggregate.js renderReportTables 的格式手寫；P95 參數決定 Release 兩個 scenario 是否達標。
write_tables() {
  local count_p95="$1" seat_p95="$2" path="$3"
  cat > "$path" <<EOF
### Release × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | $count_p95 | 600.00 | 50 | 450 | 0 | 50 | 92.00 | 通過 |
| 2 | 300.00 | 320.00 | 50 | 450 | 0 | 50 | 90.00 | 通過 |
| 3 | 310.00 | 330.00 | 50 | 450 | 0 | 50 | 74.00 | 通過 |

- 判定：通過

### Release × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | $seat_p95 | 600.00 | 50 | 450 | 0 | 50 | 81.00 | 通過 |
| 2 | 400.00 | 420.00 | 50 | 450 | 0 | 50 | 90.00 | 通過 |
| 3 | 410.00 | 430.00 | 50 | 450 | 0 | 50 | 93.00 | 通過 |

- 判定：通過

### Debug × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 9999.00 | 9999.00 | 50 | 450 | 0 | 50 | 92.00 | 未通過 |

- 判定：未通過

EOF
}

# $1 = 資料表檔，$2 = 標記區段後的額外內容（unmet-reason 區段等），$3 = 是否含門檻字樣
write_report() {
  local tables="$1" extra="$2" threshold_text="$3" path="$4"
  {
    echo "# P95 優化結果報告"
    echo
    [ "$threshold_text" = "yes" ] && echo "目標：Release 下 P95 < 500ms（201 與 409 合併計算）。"
    echo
    echo "<!-- report-tables:begin -->"
    cat "$tables"
    echo "<!-- report-tables:end -->"
    echo
    printf '%s\n' "$extra"
  } > "$path"
}

expect() {
  local name="$1" expected_status="$2"; shift 2
  local output status
  output="$(bash "$CHECKER" "$@" 2>&1)"
  status=$?
  if { [ "$expected_status" = "0" ] && [ "$status" -eq 0 ]; } || { [ "$expected_status" != "0" ] && [ "$status" -ne 0 ]; }; then
    echo "  ✓ $name"
  else
    echo "  ✗ $name (exit $status): $output"
    FAILURES=$((FAILURES + 1))
  fi
}

REASON_OK=$'<!-- unmet-reason:seat-ticket -->\n座位票剩餘延遲來自 InLockMs（p95 812.5ms，見 measure-after-c-phases-seat-ticket.json）。\n<!-- end -->'

ALL_MET="$WORK_DIR/all-met.md"; write_tables 300.00 400.00 "$ALL_MET"
SEAT_UNMET="$WORK_DIR/seat-unmet.md"; write_tables 300.00 812.50 "$SEAT_UNMET"
SEAT_EXACT_500="$WORK_DIR/seat-500.md"; write_tables 300.00 500.00 "$SEAT_EXACT_500"
SEAT_MISSING="$WORK_DIR/seat-missing.md"; write_tables 300.00 缺少 "$SEAT_MISSING"

echo "LT-REPORT-005: tables match aggregate output"
write_report "$ALL_MET" "" yes "$WORK_DIR/r1.md"
expect "identical tables pass" 0 "$WORK_DIR/r1.md" "$ALL_MET"
# docs 在 Windows checkout 為 CRLF，彙整輸出為 LF：換行差異不算竄改。
sed 's/$/\r/' "$WORK_DIR/r1.md" > "$WORK_DIR/r1-crlf.md"
expect "CRLF report with same content passes" 0 "$WORK_DIR/r1-crlf.md" "$ALL_MET"
sed 's/| 2 | 300.00 |/| 2 | 299.00 |/' "$ALL_MET" > "$WORK_DIR/tampered.md"
write_report "$WORK_DIR/tampered.md" "" yes "$WORK_DIR/r2.md"
expect "one changed number fails" 1 "$WORK_DIR/r2.md" "$ALL_MET"
head -n -3 "$ALL_MET" > "$WORK_DIR/truncated.md"
write_report "$WORK_DIR/truncated.md" "" yes "$WORK_DIR/r3.md"
expect "truncated tables fail" 1 "$WORK_DIR/r3.md" "$ALL_MET"
grep -v 'report-tables:end' "$WORK_DIR/r1.md" > "$WORK_DIR/r4.md"
expect "missing end marker fails" 1 "$WORK_DIR/r4.md" "$ALL_MET"
{ cat "$WORK_DIR/r1.md"; echo "<!-- report-tables:begin -->"; echo "<!-- report-tables:end -->"; } > "$WORK_DIR/r5.md"
expect "duplicated markers fail" 1 "$WORK_DIR/r5.md" "$ALL_MET"
expect "missing aggregate file fails" 1 "$WORK_DIR/r1.md" "$WORK_DIR/no-such.md"

echo "LT-REPORT-006: unmet reason sections"
write_report "$SEAT_UNMET" "$REASON_OK" yes "$WORK_DIR/u1.md"
expect "unmet scenario with valid reason section passes" 0 "$WORK_DIR/u1.md" "$SEAT_UNMET"
write_report "$SEAT_UNMET" "" yes "$WORK_DIR/u2.md"
expect "unmet scenario without reason section fails" 1 "$WORK_DIR/u2.md" "$SEAT_UNMET"
write_report "$SEAT_UNMET" "${REASON_OK//seat-ticket -->/count-ticket -->}" yes "$WORK_DIR/u3.md"
expect "reason section for the wrong scenario fails" 1 "$WORK_DIR/u3.md" "$SEAT_UNMET"
write_report "$SEAT_UNMET" $'<!-- unmet-reason:seat-ticket -->\n剩餘延遲來自 InLockMs（p95 812.5ms）。' yes "$WORK_DIR/u4.md"
expect "reason section without measure- file name fails" 1 "$WORK_DIR/u4.md" "$SEAT_UNMET"
write_report "$SEAT_UNMET" $'<!-- unmet-reason:seat-ticket -->\n剩餘延遲來自鎖內，見 measure-after-c-phases-seat-ticket.json。' yes "$WORK_DIR/u5.md"
expect "reason section without millisecond value fails" 1 "$WORK_DIR/u5.md" "$SEAT_UNMET"
# 證據必須在區段內：下一個 <!-- 標記之後的內容不算。
write_report "$SEAT_UNMET" $'<!-- unmet-reason:seat-ticket -->\n原因待補。\n<!-- end -->\n見 measure-x.json，812.5ms。' yes "$WORK_DIR/u6.md"
expect "evidence outside the section does not count" 1 "$WORK_DIR/u6.md" "$SEAT_UNMET"
write_report "$SEAT_UNMET" "$REASON_OK" no "$WORK_DIR/u7.md"
expect "report without 'P95 < 500ms' fails" 1 "$WORK_DIR/u7.md" "$SEAT_UNMET"
write_report "$SEAT_EXACT_500" "" yes "$WORK_DIR/u8.md"
expect "P95 exactly 500.00 counts as unmet" 1 "$WORK_DIR/u8.md" "$SEAT_EXACT_500"
write_report "$SEAT_MISSING" "" yes "$WORK_DIR/u9.md"
expect "missing Release P95 counts as unmet" 1 "$WORK_DIR/u9.md" "$SEAT_MISSING"
write_report "$ALL_MET" "" yes "$WORK_DIR/u10.md"
expect "all Release met (Debug unmet ignored): no reason section required" 0 "$WORK_DIR/u10.md" "$ALL_MET"
write_report "$ALL_MET" "" no "$WORK_DIR/u11.md"
expect "all met still requires 'P95 < 500ms' text" 1 "$WORK_DIR/u11.md" "$ALL_MET"

echo
if [ "$FAILURES" -eq 0 ]; then echo "all cases passed"; else echo "$FAILURES failure(s)"; exit 1; fi
