#!/usr/bin/env bash
# 映像 digest 釘版的靜態檢查（edge-security-headers CIP-001、003、005）：
#   兩份 Dockerfile 的外部 FROM 與 k8s/ 的第三方 image（非 projectc/ 開頭）都須為 name:tag@sha256:<64 位十六進位>，
#   且釘版文件含取得與驗證步驟的關鍵字。任一不符列出檔案與行號並非 0 結束。
# 用法：bash scripts/verify-image-pinning.sh
set -uo pipefail
cd "$(dirname "$0")/.."

readonly DOCKERFILES=(deploy/api.Dockerfile deploy/web.Dockerfile)
readonly K8S_DIR="k8s"
readonly PINNING_DOC="docs/2026-11-13-image-pinning.md"
readonly DOC_KEYWORDS=("imagetools inspect" "@sha256" "verify-image-pinning.sh")
readonly PINNED_PATTERN='^[^[:space:]]+:[^[:space:]@]+@sha256:[0-9a-f]{64}$'

failed=0
checked=0
report_violation() { echo "FAIL $1:$2 $3"; failed=$((failed + 1)); }

check_image_reference() { # check_image_reference <檔案> <行號> <映像參照>
  checked=$((checked + 1))
  [[ "$3" =~ $PINNED_PATTERN ]] || report_violation "$1" "$2" "未釘 digest：$3"
}

for dockerfile in "${DOCKERFILES[@]}"; do
  [ -f "$dockerfile" ] || { report_violation "$dockerfile" 0 "檔案不存在"; continue; }
  stages=()
  line_number=0
  while IFS= read -r line || [ -n "$line" ]; do
    line_number=$((line_number + 1))
    line="${line%$'\r'}"
    [[ "$line" =~ ^[[:space:]]*FROM[[:space:]]+(--[^[:space:]]+[[:space:]]+)*([^[:space:]]+)([[:space:]]+[Aa][Ss][[:space:]]+([^[:space:]]+))? ]] || continue
    reference="${BASH_REMATCH[2]}"
    stage_name="${BASH_REMATCH[4]:-}"
    is_stage_reference=0
    for stage in "${stages[@]:-}"; do [ "$stage" = "$reference" ] && is_stage_reference=1; done
    [ "$is_stage_reference" -eq 0 ] && [ "$reference" != "scratch" ] && check_image_reference "$dockerfile" "$line_number" "$reference"
    [ -n "$stage_name" ] && stages+=("$stage_name")
  done < "$dockerfile"
done

while IFS=: read -r manifest line_number content; do
  reference="$(sed -E 's/^[[:space:]]*-?[[:space:]]*image:[[:space:]]*//; s/[[:space:]]+#.*$//; s/^"(.*)"$/\1/' <<<"$content" | tr -d '\r')"
  [[ "$reference" == projectc/* ]] && continue
  check_image_reference "$manifest" "$line_number" "$reference"
done < <(grep -nH -E '^[[:space:]]*-?[[:space:]]*image:' "$K8S_DIR"/*.yaml | sed -E 's/^([^:]+):([0-9]+):/\1:\2:/')

if [ ! -f "$PINNING_DOC" ]; then
  report_violation "$PINNING_DOC" 0 "釘版文件不存在"
else
  for keyword in "${DOC_KEYWORDS[@]}"; do
    grep -qF -- "$keyword" "$PINNING_DOC" || report_violation "$PINNING_DOC" 0 "缺少關鍵字：$keyword"
  done
fi

if [ "$failed" -eq 0 ]; then
  echo "PASS 檢查 $checked 個外部映像參照皆已釘 digest，釘版文件關鍵字齊全"
  exit 0
fi
echo "FAIL 共 $failed 項不符（已檢查 $checked 個映像參照）"
exit 1
