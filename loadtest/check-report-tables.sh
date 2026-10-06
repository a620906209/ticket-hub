#!/usr/bin/env bash
# 報告資料表與彙整輸出的比對，以及未達標原因區段的檢查（order-placement-p95-optimization design.md 決策 7，
# LT-REPORT-005、LT-REPORT-006 的自動化部分）。非 0 結束代表報告不可作為驗收證據。
#
# 用法：bash loadtest/check-report-tables.sh <報告 .md> <彙整輸出 report-tables.md>
#
# 1. 擷取報告中 `<!-- report-tables:begin -->` 與 `<!-- report-tables:end -->`（各恰好一行）之間的內容，與彙整輸出逐字比對。
#    只正規化 CR（docs 在 Windows checkout 為 CRLF）與兩端空行，其餘任何差異都算不一致。
# 2. 從彙整輸出找出未達標的 Release scenario：任一次的 P95 欄不是 < 500 的數字（含「缺少」「缺檔」）即算未達標，
#    寧可多要求一個說明區段，也不放過缺值。每個未達標 scenario 都要有 `<!-- unmet-reason:<scenario> -->` 區段
#    （到下一個 `<!-- ` 開頭的行或檔尾），區段內至少一個 `measure-` 開頭的檔名與一個 `<數字>ms`。
# 3. 報告全文必須含 `P95 < 500ms`。
# 腳本只檢查「有沒有引用證據」，原因是否合理由人工判讀（tasks 7.4）。
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <report.md> <report-tables.md>" >&2
  exit 2
fi
REPORT_PATH="$1"
TABLES_PATH="$2"
for path in "$REPORT_PATH" "$TABLES_PATH"; do
  [ -f "$path" ] || { echo "FAIL: $path not found" >&2; exit 1; }
done

BEGIN_MARKER='<!-- report-tables:begin -->'
END_MARKER='<!-- report-tables:end -->'
FAILED=0

normalize() {
  # 去 CR，並刪除開頭與結尾的空行。
  tr -d '\r' | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'
}

REPORT_TEXT="$(tr -d '\r' < "$REPORT_PATH")"
BEGIN_COUNT="$(grep -cxF "$BEGIN_MARKER" <<< "$REPORT_TEXT" || true)"
END_COUNT="$(grep -cxF "$END_MARKER" <<< "$REPORT_TEXT" || true)"
if [ "$BEGIN_COUNT" != "1" ] || [ "$END_COUNT" != "1" ]; then
  echo "FAIL: report must contain exactly one begin and one end marker line (begin=$BEGIN_COUNT end=$END_COUNT)" >&2
  exit 1
fi
BEGIN_LINE="$(grep -nxF "$BEGIN_MARKER" <<< "$REPORT_TEXT" | cut -d: -f1)"
END_LINE="$(grep -nxF "$END_MARKER" <<< "$REPORT_TEXT" | cut -d: -f1)"
if [ "$BEGIN_LINE" -ge "$END_LINE" ]; then
  echo "FAIL: begin marker must come before end marker" >&2
  exit 1
fi

EMBEDDED="$(sed -n "$((BEGIN_LINE + 1)),$((END_LINE - 1))p" <<< "$REPORT_TEXT" | normalize)"
AGGREGATED="$(normalize < "$TABLES_PATH")"
if [ "$EMBEDDED" != "$AGGREGATED" ]; then
  echo "FAIL: report tables differ from aggregate output:" >&2
  diff <(printf '%s\n' "$AGGREGATED") <(printf '%s\n' "$EMBEDDED") >&2 || true
  FAILED=1
else
  echo "ok: report tables match aggregate output"
fi

if ! grep -qF 'P95 < 500ms' <<< "$REPORT_TEXT"; then
  echo "FAIL: report does not state the 'P95 < 500ms' threshold" >&2
  FAILED=1
fi

# 標題形如「### Release × 數量票（count-ticket）」；資料列第 1 欄為次數、第 2 欄為 P95。
UNMET_SCENARIOS="$(awk -F'|' '
  /^### / {
    is_release = ($0 ~ /^### Release /)
    scenario = $0; sub(/^.*（/, "", scenario); sub(/）.*$/, "", scenario)
    next
  }
  is_release && $2 ~ /^ *[0-9]+ *$/ {
    p95 = $3; gsub(/ /, "", p95)
    if (p95 !~ /^[0-9]+(\.[0-9]+)?$/ || p95 + 0 >= 500) unmet[scenario] = 1
  }
  END { for (s in unmet) print s }
' <<< "$AGGREGATED" | sort)"

for scenario in $UNMET_SCENARIOS; do
  SECTION="$(awk -v marker="<!-- unmet-reason:$scenario -->" '
    $0 == marker { inside = 1; found = 1; next }
    inside && /^<!-- / { inside = 0 }
    inside { print }
    END { if (!found) exit 3 }
  ' <<< "$REPORT_TEXT")" || { echo "FAIL: Release $scenario P95 not met but report has no <!-- unmet-reason:$scenario --> section" >&2; FAILED=1; continue; }
  if ! grep -qE 'measure-[a-z0-9-]+' <<< "$SECTION"; then
    echo "FAIL: unmet-reason:$scenario section cites no measure- file" >&2
    FAILED=1
  fi
  if ! grep -qE '[0-9]+(\.[0-9]+)? ?ms' <<< "$SECTION"; then
    echo "FAIL: unmet-reason:$scenario section cites no millisecond value" >&2
    FAILED=1
  fi
  [ "$FAILED" = "0" ] && echo "ok: unmet-reason:$scenario cites measurement evidence"
done

[ -z "$UNMET_SCENARIOS" ] && echo "ok: all Release scenarios met P95 < 500ms; no unmet-reason section required"
exit "$FAILED"
