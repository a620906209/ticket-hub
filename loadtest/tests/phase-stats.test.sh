#!/usr/bin/env bash
# （design.md 決策 1「取出方式」）lib/phase-stats.jq 自我測試：報告引用的分段毫秒數必須是精確百分位，不能是 Seq percentile() 的近似值。
# 以手算得出答案的假資料比對，不連 Seq。
#
# 用法：bash loadtest/tests/phase-stats.test.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROGRAM="$SCRIPT_DIR/../lib/phase-stats.jq"
FAILURES=0

expect_equal() {
  local name="$1" actual="$2" expected="$3"
  if [ "$actual" = "$expected" ]; then echo "  ✓ $name"; else echo "  ✗ $name: expected $expected, got $actual"; FAILURES=$((FAILURES + 1)); fi
}

# 11 筆 Success（InLockMs 為 1..11，順序打亂；EventLockWaitMs 為 0..10）+ 2 筆 Conflict（InLockMs 為 null）。
# 精確線性內插：1..11 的 p50 = 6、p95 = 10 + 0.5 × (11 − 10) = 10.5；0..10 的 p95 = 9.5。
# 兩個分段的 p95 必須不同——Seq 近似值曾把不同分段算成同一個數。
FIXTURE='{
  "Columns": ["Outcome", "EventLockWaitMs", "InLockMs"],
  "Rows": [
    ["Success", 5, 6], ["Success", 0, 1], ["Success", 10, 11], ["Success", 3, 4], ["Success", 7, 8],
    ["Success", 1, 2], ["Success", 9, 10], ["Success", 2, 3], ["Success", 8, 9], ["Success", 4, 5], ["Success", 6, 7],
    ["Conflict", 20, null], ["Conflict", 30, null]
  ]
}'
RESULT="$(jq -c -f "$PROGRAM" <<< "$FIXTURE")" || { echo "  ✗ jq program failed"; exit 1; }
SUCCESS='.byOutcome[] | select(.Outcome == "Success")'
CONFLICT='.byOutcome[] | select(.Outcome == "Conflict")'

echo "exact percentiles"
expect_equal "Success InLockMs p50" "$(jq "$SUCCESS | .InLockMs_p50" <<< "$RESULT")" "6"
expect_equal "Success InLockMs p95 (interpolated)" "$(jq "$SUCCESS | .InLockMs_p95" <<< "$RESULT")" "10.5"
expect_equal "Success EventLockWaitMs p95 differs from InLockMs p95" "$(jq "$SUCCESS | .EventLockWaitMs_p95" <<< "$RESULT")" "9.5"
expect_equal "Success InLockMs max" "$(jq "$SUCCESS | .InLockMs_max" <<< "$RESULT")" "11"

echo "null phases"
expect_equal "Conflict InLockMs n excludes nulls" "$(jq "$CONFLICT | .InLockMs_n" <<< "$RESULT")" "0"
expect_equal "Conflict InLockMs p95 is null, not 0" "$(jq "$CONFLICT | .InLockMs_p95" <<< "$RESULT")" "null"
expect_equal "Conflict EventLockWaitMs p50 of two values" "$(jq "$CONFLICT | .EventLockWaitMs_p50" <<< "$RESULT")" "25"

echo "overall"
expect_equal "overall samples counts every row" "$(jq '.overall.samples' <<< "$RESULT")" "13"
expect_equal "overall InLockMs n excludes nulls" "$(jq '.overall.InLockMs_n' <<< "$RESULT")" "11"
expect_equal "overall EventLockWaitMs max" "$(jq '.overall.EventLockWaitMs_max' <<< "$RESULT")" "30"

echo "single value"
SINGLE="$(jq -c -f "$PROGRAM" <<< '{"Columns":["Outcome","InLockMs"],"Rows":[["Success",42.5]]}')"
expect_equal "one sample: p95 equals the sample" "$(jq '.overall.InLockMs_p95' <<< "$SINGLE")" "42.5"

echo
if [ "$FAILURES" -eq 0 ]; then echo "all cases passed"; else echo "$FAILURES failure(s)"; exit 1; fi
