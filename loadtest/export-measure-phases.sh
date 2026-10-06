#!/usr/bin/env bash
# 從 Seq 匯出一次量測執行的分段耗時統計（order-placement-p95-optimization design.md 決策 1「取出方式」）。
# Seq 未掛 volume、容器重建後歷史清空，所以每次量測後立即匯出，不依賴之後 Seq 仍保有資料。
#
# 用法：bash loadtest/export-measure-phases.sh <起始時間 UTC ISO8601> <結束時間 UTC ISO8601> <輸出檔>
# 輸出檔慣例：loadtest/.output/measure-<LT_MEASURE_TAG>-phases-<scenario>.json；已存在時中止，不覆寫。
# 內容：全部樣本與依 Outcome 分組的各分段筆數（非 null 者）、p50、p95、max（毫秒，精確值），以及 Seq 回傳的原始值（raw）。
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <start-utc> <end-utc> <output-json>" >&2
  exit 2
fi
START_UTC="$1"
END_UTC="$2"
OUTPUT_PATH="$3"
SEQ_URL="${LT_SEQ_URL:-http://localhost:${SEQ_HOST_PORT:-8081}}"
STATS_PROGRAM="$(cd "$(dirname "$0")" && pwd)/lib/phase-stats.jq"

if [ -e "$OUTPUT_PATH" ]; then
  echo "abort: $OUTPUT_PATH already exists; results must not be overwritten" >&2
  exit 1
fi

PHASE_COLUMNS='PreTransactionMs, BeginTransactionMs, EventLockWaitMs, InLockMs, CommitMs, TotalMs'
WHERE="@MessageTemplate like 'PlaceOrder phase timings:%'"
# 一次量測最多約 500 筆；上限遠大於此，取回筆數仍與 count(*) 比對，避免被截斷而不自知。
ROW_LIMIT=100000

query_seq() {
  curl -fsS -G "$SEQ_URL/api/data" \
    --data-urlencode "q=$1" \
    --data-urlencode "rangeStartUtc=$START_UTC" \
    --data-urlencode "rangeEndUtc=$END_UTC"
}

# 百分位在 lib/phase-stats.jq 以原始值精確計算（Seq 的 percentile() 是近似值，見該檔說明）。
EXPECTED_ROWS="$(query_seq "select count(*) as samples from stream where $WHERE" | jq '.Rows[0][0] // 0')"
RAW="$(query_seq "select Outcome, $PHASE_COLUMNS from stream where $WHERE limit $ROW_LIMIT")"
ACTUAL_ROWS="$(jq '.Rows | length' <<< "$RAW")"
if [ "$ACTUAL_ROWS" != "$EXPECTED_ROWS" ]; then
  echo "error: fetched $ACTUAL_ROWS row(s) but Seq counts $EXPECTED_ROWS; not writing a partial export" >&2
  exit 1
fi

trap 'rm -f "$OUTPUT_PATH.partial"' EXIT
STATS="$(jq -c -f "$STATS_PROGRAM" <<< "$RAW")"
# 原始值（約 500 筆）放命令列會 Argument list too long，經 stdin 傳入；統計結果很小，可用 --argjson。
jq --arg start "$START_UTC" --arg end "$END_UTC" --argjson stats "$STATS" \
  '{ rangeStartUtc: $start, rangeEndUtc: $end, unit: "ms", percentileMethod: "exact, linear interpolation",
     overall: $stats.overall, byOutcome: $stats.byOutcome, raw: . }' <<< "$RAW" > "$OUTPUT_PATH.partial"
# 先寫暫存檔再改名：jq 失敗時不能留下空的目標檔，否則下次匯出會被「已存在」擋下。
mv "$OUTPUT_PATH.partial" "$OUTPUT_PATH"
echo "phases written: $OUTPUT_PATH (samples=$(jq -r '.overall.samples' "$OUTPUT_PATH"))"
