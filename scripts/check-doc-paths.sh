#!/usr/bin/env bash
# 檢查文件、agent、config 中寫到的 docs/ 路徑是否真的存在，避免搬移、改名或誤刪文件後留下失效引用。
# 用法：bash scripts/check-doc-paths.sh [<要掃描的檔案或資料夾>...]
# 預設掃描 CLAUDE.md、README.md、.claude/agents、.agents、openspec/config.yaml 與 docs/ 下的 .md；
# 不掃 openspec/changes/archive（歸檔內容是歷史紀錄，不改）。
# 只比對以 `docs/` 開頭、副檔名為 md／html／json／sh 的路徑，或以 `/` 結尾的資料夾路徑。
set -euo pipefail

cd "$(dirname "$0")/.."

targets=("$@")
[ "${#targets[@]}" -gt 0 ] || targets=(CLAUDE.md README.md .claude/agents .agents openspec/config.yaml docs)

missing=0
while IFS= read -r hit; do
  file="${hit%%:*}"
  path="${hit#*:}"
  path="${path%.}"
  [ -e "$path" ] && continue
  echo "MISSING: $path （引用於 $file）"
  missing=1
done < <(grep -rIoE --include='*.md' --include='*.yaml' --include='*.html' \
  'docs/[A-Za-z0-9._/-]+\.(md|html|json|sh)|docs/[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*/' \
  "${targets[@]}" 2>/dev/null | sort -u)

if [ "$missing" -ne 0 ]; then
  echo "FAIL: 有失效的 docs 路徑引用" >&2
  exit 1
fi
echo "OK: 所有 docs 路徑引用都存在"
