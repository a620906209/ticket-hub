#!/usr/bin/env bash
# 產生 CLAUDE.md 要求的還原驗證與 mutation 證據原文。輸出只濾掉建置 warning，可直接貼進 tasks.md。
#
#   restore --path <路徑> [--path <路徑>...] -- <測試命令...>
#     以 git stash 暫時移開尚未 commit 的正式程式碼，確認測試因此失敗。
#   mutate --file <檔案> --from <原字串> --to <新字串> -- <測試命令...>
#     暫時套用一個改動，確認測試因此失敗。一次一個，合併套用時改動可能互相遮蔽。
#
# 退出碼：0 測試如預期失敗；1 測試仍通過，證據不成立；
#         2 無法判定（參數錯誤、環境或編譯錯誤）；3 stash pop 或檔案還原失敗，須人工處理。
#
# 測試失敗以 dotnet test 的 `Failed!` 摘要行判定，沒有這行的非零結束一律視為環境或編譯錯誤。
set -u

readonly EXIT_TEST_FAILED_AS_EXPECTED=0
readonly EXIT_TEST_STILL_PASSES=1
readonly EXIT_INCONCLUSIVE=2
readonly EXIT_RESTORE_FAILED=3

print_usage_and_exit() {
  sed -n '4,7p' "$0" >&2
  exit "$EXIT_INCONCLUSIVE"
}

run_test_command() {
  echo "\$ $*"
  local log
  log="$(mktemp)" || return "$EXIT_INCONCLUSIVE"
  "$@" > "$log" 2>&1
  local test_status=$?
  grep -v ': warning ' "$log"
  local has_failure_summary=1
  grep -q 'Failed!' "$log" && has_failure_summary=0
  rm -f "$log"

  if [ "$test_status" -eq 0 ]; then
    echo "結果：測試仍通過，證據不成立"
    return "$EXIT_TEST_STILL_PASSES"
  fi
  if [ "$has_failure_summary" -ne 0 ]; then
    echo "結果：沒有測試失敗摘要，視為環境或編譯錯誤"
    return "$EXIT_INCONCLUSIVE"
  fi
  echo "結果：測試如預期失敗"
  return "$EXIT_TEST_FAILED_AS_EXPECTED"
}

pop_stash() {
  trap - INT TERM
  if [ "$(git rev-parse -q --verify refs/stash)" != "$created_stash" ]; then
    echo "stash 頂端已改變，未執行 pop：修改仍在 stash（$created_stash），請人工處理" >&2
    return "$EXIT_RESTORE_FAILED"
  fi
  echo "\$ git stash pop"
  if ! git stash pop; then
    echo "git stash pop 失敗：stash 已保留，請人工處理，不要自行修復工作樹" >&2
    return "$EXIT_RESTORE_FAILED"
  fi
}

run_restore() {
  local paths=()
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do
    [ "$1" = "--path" ] && [ $# -ge 2 ] || print_usage_and_exit
    paths+=("$2")
    shift 2
  done
  [ $# -ge 2 ] && [ "${#paths[@]}" -gt 0 ] || print_usage_and_exit
  shift

  local stash_before
  stash_before="$(git rev-parse -q --verify refs/stash)"
  echo "\$ git stash push -u -- ${paths[*]}"
  git stash push -u -- "${paths[@]}" || exit "$EXIT_INCONCLUSIVE"
  created_stash="$(git rev-parse -q --verify refs/stash)"
  if [ "$stash_before" = "$created_stash" ]; then
    echo "指定路徑沒有可移開的修改" >&2
    exit "$EXIT_INCONCLUSIVE"
  fi
  trap 'pop_stash || exit "$EXIT_RESTORE_FAILED"; exit "$EXIT_INCONCLUSIVE"' INT TERM
  echo "\$ git stash list"
  git stash list

  run_test_command "$@"
  local result=$?
  pop_stash || exit "$EXIT_RESTORE_FAILED"
  exit "$result"
}

# 以 node 做字面取代：sed 會把字串當正規式，也可能改動 CRLF。
replace_exactly_once() {
  node -e '
    const fs = require("fs");
    const [file, from, to] = process.argv.slice(1);
    const parts = fs.readFileSync(file, "latin1").split(Buffer.from(from).toString("latin1"));
    if (parts.length !== 2) {
      console.error(`預期原字串恰好出現 1 次，實際 ${parts.length - 1} 次`);
      process.exit(1);
    }
    fs.writeFileSync(file, parts.join(Buffer.from(to).toString("latin1")), "latin1");
  ' "$1" "$2" "$3"
}

restore_mutated_file() {
  local exit_status=$?
  trap - EXIT
  if ! cp -p "$backup" "$mutated_file"; then
    echo "還原失敗：$mutated_file 仍是 mutation 內容，備份保留於：$backup" >&2
    exit "$EXIT_RESTORE_FAILED"
  fi
  rm -f "$backup"
  exit "$exit_status"
}

run_mutate() {
  local file="" from="" to=""
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do
    [ $# -ge 2 ] || print_usage_and_exit
    case "$1" in
      --file) file="$2" ;;
      --from) from="$2" ;;
      --to) to="$2" ;;
      *) print_usage_and_exit ;;
    esac
    shift 2
  done
  [ $# -ge 2 ] && [ -n "$file" ] && [ -n "$from" ] || print_usage_and_exit
  shift
  [ -f "$file" ] || { echo "找不到檔案：$file" >&2; exit "$EXIT_INCONCLUSIVE"; }

  # 還原用備份而非 git checkout：檔案可能有尚未 commit 的修改。
  backup="$(mktemp)" || { echo "無法建立備份，未套用 mutation" >&2; exit "$EXIT_INCONCLUSIVE"; }
  if ! cp -p "$file" "$backup"; then
    rm -f "$backup"
    echo "無法建立備份，未套用 mutation" >&2
    exit "$EXIT_INCONCLUSIVE"
  fi
  mutated_file="$file"
  trap restore_mutated_file EXIT
  trap 'exit "$EXIT_INCONCLUSIVE"' INT TERM

  replace_exactly_once "$file" "$from" "$to" || exit "$EXIT_INCONCLUSIVE"
  echo "mutation: $from -> $to"
  run_test_command "$@"
  exit $?
}

[ $# -ge 1 ] || print_usage_and_exit
mode="$1"
shift
case "$mode" in
  restore) run_restore "$@" ;;
  mutate) run_mutate "$@" ;;
  *) print_usage_and_exit ;;
esac
