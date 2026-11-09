#!/usr/bin/env bash
# check-doc-paths.sh 的自我測試：引用不存在的 docs 路徑必須失敗，引用存在的路徑必須通過。
# 用法：bash scripts/tests/check-doc-paths.test.sh
set -uo pipefail

cd "$(dirname "$0")/../.."
fixture=".tmp-check-doc-paths"
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture"
failures=0

expect() { # <描述> <預期結束碼> <檔案內容>
  printf '%s\n' "$3" > "$fixture/sample.md"
  bash scripts/check-doc-paths.sh "$fixture" > "$fixture/out.txt" 2>&1
  actual=$?
  if [ "$actual" -ne "$2" ]; then
    echo "FAIL: $1（預期 $2，實際 $actual）"; cat "$fixture/out.txt"; failures=1
  else
    echo "PASS: $1"
  fi
}

expect "引用存在的文件" 0 '見 `docs/api-conventions.md`'
expect "引用存在的資料夾" 0 '見 `docs/todo/`'
expect "引用不存在的文件" 1 '見 `docs/does-not-exist.md`'
expect "引用不存在的資料夾" 1 '見 `docs/no-such-folder/`'
expect "沒有任何 docs 引用" 0 '沒有路徑'

[ "$failures" -eq 0 ] && echo "ALL PASSED" || { echo "SOME FAILED"; exit 1; }
