#!/usr/bin/env bash
# 壓測期間的資料庫等待事件取樣（order-placement-p95-optimization design.md 決策 2，LT-MEASURE-007、010、011）。
# 每 200ms 以 sql/db-waits.sql 查一次 pg_stat_activity，依 state／wait_event 分組計數，輸出 CSV。
# 只看分布；耗時以應用端分段計時為準，取樣時間戳只用來對齊壓測時段（WSL2 牆上時鐘會倒退）。
# 取樣本身占 1 條連線，報告須註明。
#
# 用法：bash loadtest/sample-db-waits.sh <LT_MEASURE_TAG> <scenario> <秒數>
# 輸出：loadtest/.output/measure-<tag>-db-waits-<scenario>.csv；已存在時中止，不覆寫。非 0 結束即該次量測無效。
#
# 可覆寫（tests/sample-db-waits.test.sh 用來指向一次性容器）：
#   LT_DB_SAMPLER_PREFIX  取樣連線的執行前綴，必須設定 PGAPPNAME=lt-db-waits（預設 docker compose exec -T -e PGAPPNAME=lt-db-waits db）
#   LT_DB_ADMIN_PREFIX    檢查／清理用管理連線的執行前綴（預設 docker compose exec -T -e PGAPPNAME=lt-db-waits-cleanup db）
#   LT_OUTPUT_DIR         輸出目錄（預設 loadtest/.output）
set -uo pipefail

APP_NAME='lt-db-waits'
NAME_PATTERN='^[a-z0-9-]{1,32}$'
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SQL_PATH="$SCRIPT_DIR/sql/db-waits.sql"
SAMPLER_PREFIX="${LT_DB_SAMPLER_PREFIX:-docker compose exec -T -e PGAPPNAME=$APP_NAME db}"
ADMIN_PREFIX="${LT_DB_ADMIN_PREFIX:-docker compose exec -T -e PGAPPNAME=$APP_NAME-cleanup db}"
OUTPUT_DIR="${LT_OUTPUT_DIR:-$SCRIPT_DIR/.output}"
CSV_HEADER='sampled_at_utc,state,wait_event_type,wait_event,connections'
# 帳號與資料庫取自 db 容器自己的環境變數，經容器內 unix socket 連線，帳密不出現在主機命令列。
PSQL_IN_CONTAINER='psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <LT_MEASURE_TAG> <scenario> <seconds>" >&2
  exit 2
fi
TAG="$1"
SCENARIO="$2"
SECONDS_TO_SAMPLE="$3"
if ! [[ "$TAG" =~ $NAME_PATTERN ]]; then echo "abort: LT_MEASURE_TAG must match $NAME_PATTERN" >&2; exit 2; fi
if ! [[ "$SCENARIO" =~ $NAME_PATTERN ]]; then echo "abort: scenario must match $NAME_PATTERN" >&2; exit 2; fi
if ! [[ "$SECONDS_TO_SAMPLE" =~ ^[0-9]+$ ]] || [ "$SECONDS_TO_SAMPLE" -lt 1 ] || [ "$SECONDS_TO_SAMPLE" -gt 600 ]; then
  echo "abort: seconds must be an integer between 1 and 600" >&2
  exit 2
fi

OUTPUT_PATH="$OUTPUT_DIR/measure-$TAG-db-waits-$SCENARIO.csv"
PARTIAL_PATH="$OUTPUT_PATH.partial"
SAMPLE_COUNT=$((SECONDS_TO_SAMPLE * 5))
if [ -e "$OUTPUT_PATH" ] || [ -e "$PARTIAL_PATH" ]; then
  echo "abort: $OUTPUT_PATH (or .partial) already exists; results must not be overwritten" >&2
  exit 1
fi

run_admin_sql() {
  $ADMIN_PREFIX sh -c "$PSQL_IN_CONTAINER -At" <<< "$1"
}

count_sampler_backends() {
  run_admin_sql "SELECT count(*) FROM pg_stat_activity WHERE application_name = '$APP_NAME' AND datname = current_database()"
}

# 上一次的殘留可能不是這次能判斷來源的連線，只提示、不代為終止。
LEFTOVER="$(count_sampler_backends)" || { echo "abort: cannot connect to database for pre-check" >&2; exit 1; }
if [ "$LEFTOVER" != "0" ]; then
  echo "abort: $LEFTOVER '$APP_NAME' backend(s) already exist; terminate them first:" >&2
  echo "  SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '$APP_NAME' AND datname = current_database();" >&2
  exit 1
fi

SAMPLER_PID=""
IS_CLEANED_UP=0
# 中止主機端的 docker exec client 不保證會結束容器內的 psql，所以一律以終止 backend 收尾，並確認殘留為 0。
cleanup() {
  [ "$IS_CLEANED_UP" = "1" ] && return
  IS_CLEANED_UP=1
  [ -n "$SAMPLER_PID" ] && kill "$SAMPLER_PID" 2>/dev/null
  run_admin_sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '$APP_NAME' AND datname = current_database() AND pid <> pg_backend_pid()" >/dev/null
  local residual=""
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    residual="$(count_sampler_backends)"
    [ "$residual" = "0" ] && return 0
    sleep 0.5
  done
  echo "error: '$APP_NAME' backend(s) still alive after cleanup: $(run_admin_sql "SELECT string_agg(pid::text, ' ') FROM pg_stat_activity WHERE application_name = '$APP_NAME' AND datname = current_database()")" >&2
  return 1
}

on_exit() {
  local status=$?
  cleanup || status=1
  [ "$status" -ne 0 ] && rm -f "$PARTIAL_PATH"
  exit "$status"
}
trap on_exit EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

{ cat "$SQL_PATH"; printf '\n\\watch i=0.2 c=%s\n' "$SAMPLE_COUNT"; } \
  | $SAMPLER_PREFIX sh -c "$PSQL_IN_CONTAINER --csv -t -f -" > "$PARTIAL_PATH" &
SAMPLER_PID=$!

# application name 沒設成功時，取樣連線無法以名稱清理；3 秒內確認恰好 1 條，否則立即中止（LT-MEASURE-011）。
IS_NAME_CONFIRMED=0
for _ in 1 2 3 4 5 6; do
  if [ "$(count_sampler_backends)" = "1" ]; then IS_NAME_CONFIRMED=1; break; fi
  sleep 0.5
done
if [ "$IS_NAME_CONFIRMED" != "1" ]; then
  echo "abort: expected exactly 1 '$APP_NAME' backend within 3s; sampler prefix must set PGAPPNAME=$APP_NAME" >&2
  echo "warning: an unnamed sampler connection may keep running for up to ${SECONDS_TO_SAMPLE}s and cannot be terminated by name" >&2
  exit 1
fi

wait "$SAMPLER_PID"
SAMPLER_STATUS=$?
SAMPLER_PID=""
if [ "$SAMPLER_STATUS" -ne 0 ]; then
  echo "error: sampler psql exited with $SAMPLER_STATUS" >&2
  exit 1
fi

SAMPLE_POINTS="$(grep -v '^$' "$PARTIAL_PATH" | cut -d, -f1 | sort -u | wc -l)"
if [ "$SAMPLE_POINTS" -ne "$SAMPLE_COUNT" ]; then
  echo "error: got $SAMPLE_POINTS sample point(s), expected $SAMPLE_COUNT" >&2
  exit 1
fi

{ echo "$CSV_HEADER"; grep -v '^$' "$PARTIAL_PATH"; } > "$OUTPUT_PATH"
rm -f "$PARTIAL_PATH"
echo "db waits written: $OUTPUT_PATH (samples=$SAMPLE_POINTS)"
