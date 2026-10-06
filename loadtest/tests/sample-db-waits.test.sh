#!/usr/bin/env bash
# [LT-MEASURE-007] [LT-MEASURE-010] [LT-MEASURE-011] sample-db-waits.sh 自我測試（order-placement-p95-optimization design.md 決策 2）。
# 在一次性的 postgres:16-alpine 容器上執行，不連開發用 db 服務（CLAUDE.md）。
# 取樣連線若在中斷後殘留，會在正式壓測時多占一條連線、並把自己算進等待分布，所以清理與「名稱沒設成功」都要測。
#
# 用法：bash loadtest/tests/sample-db-waits.test.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SAMPLER="$SCRIPT_DIR/../sample-db-waits.sh"
WORK_DIR="$(mktemp -d)"
FAILURES=0
CONTAINER=""

cleanup() {
  [ -n "$CONTAINER" ] && docker rm -f "$CONTAINER" >/dev/null 2>&1
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

pass() { echo "  ✓ $1"; }
fail_case() { echo "  ✗ $1"; FAILURES=$((FAILURES + 1)); }

run_sql() {
  docker exec -i "$CONTAINER" sh -c 'psql -X -At -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"' <<< "$1"
}

count_sampler_backends() {
  run_sql "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'lt-db-waits'"
}

# 在容器內以 docker exec -d 開一條保持連線的 psql（先送 $2 再閒置），隨容器移除；不在主機留下長時間的背景工作。
open_held_connection() {
  docker exec -d -e PGAPPNAME="$1" -e HELD_SQL="$2" "$CONTAINER" \
    sh -c '(printf "%s\n" "$HELD_SQL"; sleep 600) | psql -X -q -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null'
}

wait_until_sql_equals() {
  for _ in $(seq 1 20); do
    [ "$(run_sql "$1")" = "$2" ] && return 0
    sleep 0.25
  done
  return 1
}

# 腳本結束後 5 秒內殘留必須歸 0（LT-MEASURE-010）。
assert_no_residual_within_5s() {
  local label="$1" residual
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    residual="$(count_sampler_backends)"
    [ "$residual" = "0" ] && { pass "$label: no lt-db-waits backend left"; return; }
    sleep 0.5
  done
  fail_case "$label: $residual lt-db-waits backend(s) left after 5s"
}

# 直接執行 bash（不包在 function 裡放背景）：背景執行 function 時 $! 是 subshell，訊號送不到腳本本身。
run_sampler() {
  bash "$SAMPLER" "$@"
}

echo "starting disposable postgres:16-alpine"
CONTAINER="$(docker run -d -e POSTGRES_PASSWORD=lt-test -e POSTGRES_USER=lt -e POSTGRES_DB=ltdb postgres:16-alpine)" || exit 1
# 初始化期間的暫時伺服器只聽 unix socket；以 TCP 檢查才代表正式伺服器已就緒。
for _ in $(seq 1 60); do docker exec "$CONTAINER" pg_isready -q -h 127.0.0.1 && break; sleep 1; done
docker exec "$CONTAINER" pg_isready -q -h 127.0.0.1 || { echo "postgres not ready"; exit 1; }

export LT_OUTPUT_DIR="$WORK_DIR"
export LT_DB_SAMPLER_PREFIX="docker exec -i -e PGAPPNAME=lt-db-waits $CONTAINER"
export LT_DB_ADMIN_PREFIX="docker exec -i -e PGAPPNAME=lt-db-waits-cleanup $CONTAINER"

# 一條停在交易中的連線：BEGIN 後不送 COMMIT。
open_held_connection lt-idle-holder 'BEGIN; SELECT 1;'
wait_until_sql_equals "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'lt-idle-holder' AND state = 'idle in transaction'" 1 \
  || { echo "idle in transaction connection not established"; exit 1; }

echo "case: normal sampling (LT-MEASURE-007)"
OUTPUT="$WORK_DIR/measure-t1-db-waits-test.csv"
if run_sampler t1 test 1 >"$WORK_DIR/normal.log" 2>&1; then pass "exit 0"; else fail_case "exit non-zero: $(cat "$WORK_DIR/normal.log")"; fi
if [ -f "$OUTPUT" ]; then
  SAMPLE_POINTS="$(tail -n +2 "$OUTPUT" | cut -d, -f1 | sort -u | wc -l)"
  [ "$SAMPLE_POINTS" -ge 4 ] && pass "≥ 4 sample points ($SAMPLE_POINTS)" || fail_case "only $SAMPLE_POINTS sample point(s)"
  [ "$(head -1 "$OUTPUT")" = "sampled_at_utc,state,wait_event_type,wait_event,connections" ] && pass "csv header" || fail_case "unexpected header: $(head -1 "$OUTPUT")"
  grep -q ',idle in transaction,' "$OUTPUT" && pass "contains idle in transaction connection" || fail_case "idle in transaction row missing"
  # 測試庫只有 idle 連線與取樣工具自己；出現 active 列代表取樣連線把自己算進去了。
  if grep -q ',active,' "$OUTPUT"; then fail_case "sampler counted itself (active row present)"; else pass "sampler itself excluded"; fi
else
  fail_case "output file not created: $(cat "$WORK_DIR/normal.log")"
fi
assert_no_residual_within_5s "normal"

echo "case: output already exists"
BEFORE_SUM="$(md5sum < "$OUTPUT" 2>/dev/null)"
if run_sampler t1 test 1 >"$WORK_DIR/exists.log" 2>&1; then fail_case "exit 0 although output exists"; else pass "aborted"; fi
[ "$(md5sum < "$OUTPUT" 2>/dev/null)" = "$BEFORE_SUM" ] && pass "existing output untouched" || fail_case "existing output modified"

echo "case: invalid tag"
if run_sampler 'a/b' test 1 >"$WORK_DIR/tag.log" 2>&1; then fail_case "exit 0 with invalid tag"; else pass "aborted"; fi

echo "case: leftover lt-db-waits backend before start"
open_held_connection lt-db-waits 'SELECT 1;'
wait_until_sql_equals "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'lt-db-waits'" 1 \
  || fail_case "could not create leftover backend"
if run_sampler t2 test 1 >"$WORK_DIR/leftover.log" 2>&1; then fail_case "exit 0 with leftover backend"; else pass "aborted"; fi
[ ! -e "$WORK_DIR/measure-t2-db-waits-test.csv" ] && pass "no output" || fail_case "output produced"
# 中止時不替使用者終止既有連線（可能不是本工具留下的），只提示清理。
[ "$(count_sampler_backends)" = "1" ] && pass "leftover backend not terminated by pre-check" || fail_case "pre-check terminated leftover backend"
run_sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'lt-db-waits'" >/dev/null
assert_no_residual_within_5s "leftover cleanup"

for signal in INT TERM; do
  tag="sig-$(echo "$signal" | tr 'A-Z' 'a-z')"
  echo "case: SIG$signal during sampling (LT-MEASURE-010)"
  # set -m：非互動 shell 的背景工作預設忽略 SIGINT，開 job control 才能讓腳本真的收到。
  set -m
  bash "$SAMPLER" "$tag" test 30 >"$WORK_DIR/$tag.log" 2>&1 &
  SAMPLER_PID=$!
  set +m
  wait_until_sql_equals "SELECT count(*) FROM pg_stat_activity WHERE application_name = 'lt-db-waits'" 1 \
    || fail_case "SIG$signal: sampler backend never appeared"
  sleep 1
  kill "-$signal" "$SAMPLER_PID"
  wait "$SAMPLER_PID"
  STATUS=$?
  [ "$STATUS" -ne 0 ] && pass "exit non-zero ($STATUS)" || fail_case "exit 0 after SIG$signal"
  [ ! -e "$WORK_DIR/measure-$tag-db-waits-test.csv" ] && [ ! -e "$WORK_DIR/measure-$tag-db-waits-test.csv.partial" ] \
    && pass "no output" || fail_case "output produced after interrupt"
  assert_no_residual_within_5s "SIG$signal"
done

echo "case: application name not applied (LT-MEASURE-011)"
if LT_DB_SAMPLER_PREFIX="docker exec -i $CONTAINER" run_sampler noname test 30 >"$WORK_DIR/noname.log" 2>&1; then
  fail_case "exit 0 without application name"
else
  pass "aborted"
fi
[ ! -e "$WORK_DIR/measure-noname-db-waits-test.csv" ] && pass "no output" || fail_case "output produced"
assert_no_residual_within_5s "no application name"

echo
if [ "$FAILURES" -eq 0 ]; then echo "all cases passed"; else echo "$FAILURES failure(s)"; exit 1; fi
