#!/usr/bin/env bash
# 證據腳本若把環境錯誤或沒失敗的測試回報成成功，審查就會採信無效的還原驗證，所以每條路徑都驗退出碼與工作樹狀態。
set -u

script="$(cd "$(dirname "$0")/.." && pwd)/verify-test-evidence.sh"
failures=0

report_failure() {
  echo "FAIL: $1"
  failures=$((failures + 1))
}

assert_equal() {
  [ "$2" = "$3" ] || report_failure "$1: expected [$3], got [$2]"
}

assert_output_contains() {
  case "$2" in
    *"$3"*) ;;
    *) report_failure "$1: output lacks [$3]" ;;
  esac
}

# 依 src.txt 是否含 fixed 模擬目標測試的結果，訊息格式比照 dotnet test 摘要行。
target_test='if grep -q fixed src.txt; then echo "Passed!  - Failed:     0, Passed:     1"; else echo "  Failed Foo.Bar"; echo "Failed!  - Failed:     1, Passed:     0"; exit 1; fi'
always_passes='echo "Passed!  - Failed:     0, Passed:     1"'
environment_error='echo "error CS1002: ; expected"; exit 1'

create_repo() {
  repo="$(mktemp -d)"
  cd "$repo" || exit 1
  git init -q
  git config user.email test@example.com
  git config user.name test
  git config core.autocrlf false
  echo broken > src.txt
  echo other > other.txt
  git add .
  git commit -qm init
}

run_script() {
  output="$(bash "$script" "$@" 2>&1)"
  status=$?
}

# restore：拿掉修改後測試因目標行為失敗，才算證明測試有效。
create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c "$target_test"
assert_equal "restore caught status" "$status" 0
assert_equal "restore caught file" "$(cat src.txt)" fixed
assert_equal "restore caught stash" "$(git stash list | wc -l | tr -d ' ')" 0
assert_output_contains "restore caught" "$output" "git stash push -u -- src.txt"
assert_output_contains "restore caught" "$output" "git stash list"
assert_output_contains "restore caught" "$output" "Failed Foo.Bar"
assert_output_contains "restore caught" "$output" "Failed!  - Failed:     1"
assert_output_contains "restore caught" "$output" "git stash pop"

create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c "$always_passes"
assert_equal "restore survived status" "$status" 1
assert_equal "restore survived file" "$(cat src.txt)" fixed

# 編譯或環境錯誤不是 RED。
create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c "$environment_error"
assert_equal "restore environment status" "$status" 2
assert_equal "restore environment file" "$(cat src.txt)" fixed

# 指定路徑沒有修改時不會產生 stash，不得誤把別人的 stash 彈出。
create_repo
echo earlier > other.txt
git stash push -q -- other.txt
run_script restore --path src.txt -- sh -c "$target_test"
assert_equal "restore nothing status" "$status" 2
assert_equal "restore nothing stash" "$(git stash list | wc -l | tr -d ' ')" 1

create_repo
echo earlier > other.txt
git stash push -q -- other.txt
echo fixed > src.txt
run_script restore --path src.txt -- sh -c "$target_test"
assert_equal "restore earlier status" "$status" 0
assert_equal "restore earlier stash" "$(git stash list | wc -l | tr -d ' ')" 1
assert_equal "restore earlier file" "$(cat other.txt)" other

# 未追蹤檔案也要一起移開，否則新增的正式程式碼仍在，測試不會失敗。
create_repo
echo fixed > extra.txt
run_script restore --path extra.txt -- sh -c 'test ! -e extra.txt && { echo "Failed!  - Failed:     1"; exit 1; }'
assert_equal "restore untracked status" "$status" 0
assert_equal "restore untracked file" "$(cat extra.txt)" fixed

# pop 失敗時須停下來保留 stash，不得自行修復工作樹。
create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c 'echo dirty > src.txt; echo "Failed!  - Failed:     1"; exit 1'
assert_equal "restore pop failure status" "$status" 3
assert_equal "restore pop failure stash" "$(git stash list | wc -l | tr -d ' ')" 1
assert_equal "restore pop failure file" "$(cat src.txt)" dirty

# 測試期間 stash 頂端若被其他操作改變，pop 會彈出別人的 stash 而修正仍留在 stash 內，所以須停止並保留兩筆。
create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c 'echo concurrent > other.txt; git stash push -q -m concurrent -- other.txt; echo "Failed!  - Failed:     1"; exit 1'
assert_equal "restore stash replaced status" "$status" 3
assert_equal "restore stash replaced count" "$(git stash list | wc -l | tr -d ' ')" 2
assert_output_contains "restore stash replaced" "$(git stash list | head -1)" concurrent
assert_output_contains "restore stash replaced" "$output" "stash 頂端已改變"

# 中斷時同樣要彈回修改；彈不回來時不得只回報「無法判定」，否則使用者不知道工作樹未還原。
create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c 'kill -TERM $PPID; exit 1'
assert_equal "restore interrupted status" "$status" 2
assert_equal "restore interrupted file" "$(cat src.txt)" fixed
assert_equal "restore interrupted stash" "$(git stash list | wc -l | tr -d ' ')" 0

create_repo
echo fixed > src.txt
run_script restore --path src.txt -- sh -c 'echo dirty > src.txt; kill -TERM $PPID; exit 1'
assert_equal "restore interrupted pop failure status" "$status" 3
assert_equal "restore interrupted pop failure stash" "$(git stash list | wc -l | tr -d ' ')" 1

# mutate：一次只能一個改動，因為合併套用時某個改動可能遮蔽另一個。
create_repo
printf 'broken\r\nkeep\r\n' > src.txt
run_script mutate --file src.txt --from keep --to fixed -- sh -c 'if grep -q fixed src.txt; then echo "  Failed Foo.Bar"; echo "Failed!  - Failed:     1"; exit 1; fi'
assert_equal "mutate caught status" "$status" 0
assert_equal "mutate caught file" "$(od -c src.txt | head -3)" "$(printf 'broken\r\nkeep\r\n' | od -c | head -3)"
assert_output_contains "mutate caught" "$output" "Failed Foo.Bar"
assert_output_contains "mutate caught" "$output" "mutation: keep -> fixed"

create_repo
printf 'broken\nkeep\n' > src.txt
run_script mutate --file src.txt --from keep --to fixed -- sh -c "$always_passes"
assert_equal "mutate survived status" "$status" 1
assert_equal "mutate survived file" "$(tail -1 src.txt)" keep

create_repo
run_script mutate --file src.txt --from absent --to fixed -- sh -c 'touch ran.txt'
assert_equal "mutate no match status" "$status" 2
test ! -e ran.txt || report_failure "mutate no match: test command ran"

create_repo
printf 'same\nsame\n' > src.txt
run_script mutate --file src.txt --from same --to fixed -- sh -c 'touch ran.txt'
assert_equal "mutate ambiguous status" "$status" 2
test ! -e ran.txt || report_failure "mutate ambiguous: test command ran"
assert_equal "mutate ambiguous file" "$(head -1 src.txt)" same

create_repo
printf 'broken\nkeep\n' > src.txt
run_script mutate --file src.txt --from keep --to fixed -- sh -c "$environment_error"
assert_equal "mutate environment status" "$status" 2
assert_equal "mutate environment file" "$(tail -1 src.txt)" keep

# 讓指定命令的第 N 次呼叫失敗，模擬磁碟或權限問題。
create_failing_command() {
  local command_name="$1" failing_call="$2" real_command
  real_command="$(command -v "$command_name")"
  shim_directory="$(mktemp -d)"
  cat > "$shim_directory/$command_name" <<SHIM
#!/usr/bin/env bash
count=\$((\$(cat "$shim_directory/count" 2>/dev/null || echo 0) + 1))
echo "\$count" > "$shim_directory/count"
[ "\$count" -eq "$failing_call" ] && exit 1
exec "$real_command" "\$@"
SHIM
  chmod +x "$shim_directory/$command_name"
}

# 沒有備份就無法還原，正式檔案不得被改動，測試也不該執行。
create_repo
printf 'broken\nkeep\n' > src.txt
create_failing_command mktemp 1
PATH="$shim_directory:$PATH" run_script mutate --file src.txt --from keep --to fixed -- sh -c 'touch ran.txt; echo "Failed!  - Failed:     1"; exit 1'
assert_equal "mutate backup mktemp failure status" "$status" 2
assert_equal "mutate backup mktemp failure file" "$(tail -1 src.txt)" keep
test ! -e ran.txt || report_failure "mutate backup mktemp failure: test command ran"

create_repo
printf 'broken\nkeep\n' > src.txt
create_failing_command cp 1
PATH="$shim_directory:$PATH" run_script mutate --file src.txt --from keep --to fixed -- sh -c 'touch ran.txt; echo "Failed!  - Failed:     1"; exit 1'
assert_equal "mutate backup copy failure status" "$status" 2
assert_equal "mutate backup copy failure file" "$(tail -1 src.txt)" keep
test ! -e ran.txt || report_failure "mutate backup copy failure: test command ran"

# 還原失敗時正式檔案仍是 mutation 內容，即使測試如預期失敗也不得回報成功，且要留下備份供人工還原。
create_repo
printf 'broken\nkeep\n' > src.txt
create_failing_command cp 2
PATH="$shim_directory:$PATH" run_script mutate --file src.txt --from keep --to fixed -- sh -c 'echo "Failed!  - Failed:     1"; exit 1'
assert_equal "mutate restore failure status" "$status" 3
assert_output_contains "mutate restore failure" "$output" "還原失敗"
backup_path="$(printf '%s\n' "$output" | sed -n 's/^.*備份保留於：//p')"
assert_equal "mutate restore failure backup" "$(tail -1 "$backup_path" 2>/dev/null)" keep

create_repo
run_script unknown
assert_equal "unknown mode status" "$status" 2

if [ "$failures" -gt 0 ]; then
  echo "$failures assertion(s) failed"
  exit 1
fi
echo "all assertions passed"
