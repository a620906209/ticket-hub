#!/usr/bin/env bash
# 把已完成的待辦從 docs/todo/ 搬到 docs/archive/，並把其他文件、腳本中的舊路徑同步改成新路徑。
# 用法：bash scripts/archive-todo.sh <檔名或 docs/todo/ 下的路徑>
# 不判斷待辦是否真的完成，由呼叫者（使用者）決定；不 commit。
# 不改 openspec/changes/archive（歸檔內容是歷史紀錄）。
set -euo pipefail

cd "$(dirname "$0")/.."

[ "$#" -eq 1 ] || { echo "用法：bash scripts/archive-todo.sh <檔名>" >&2; exit 2; }

name="$(basename "$1")"
source_path="docs/todo/$name"
target_path="docs/archive/$name"

[ -f "$source_path" ] || { echo "找不到 $source_path" >&2; exit 2; }
[ ! -e "$target_path" ] || { echo "$target_path 已存在，請先處理" >&2; exit 2; }

mkdir -p docs/archive
git mv "$source_path" "$target_path"

while IFS= read -r file; do
  sed -i "s#$source_path#$target_path#g" "$file"
  echo "已更新引用：$file"
done < <(grep -rIlF --include='*.md' --include='*.yaml' --include='*.sh' --include='*.html' \
  "$source_path" CLAUDE.md README.md .claude .agents openspec/config.yaml docs scripts 2>/dev/null || true)

bash scripts/check-doc-paths.sh
echo "已搬移：$source_path → $target_path（尚未 commit）"
