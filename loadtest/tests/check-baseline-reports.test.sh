#!/usr/bin/env bash
# [LT-REPORT-007] check-baseline-reports.sh 自我測試（order-placement-p95-phase2 tasks 4.5，基準檔契約的自動化覆蓋）。
# 第一階段報告「未修改」是第二階段驗收的前提；基準若在驗收途中漂移（例如重新解析已前移的 master），
# 中途被改過的報告也可能判定通過，所以每個失敗分支都要被擋下。全部在暫存 git repository 進行，不碰本專案的 repo。
#
# 用法：bash loadtest/tests/check-baseline-reports.test.sh
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CHECKER="$SCRIPT_DIR/../check-baseline-reports.sh"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
FAILURES=0
REPORT_A=docs/report-a.md
REPORT_B=docs/report-b.md

fail() {
  echo "  ✗ $1"
  FAILURES=$((FAILURES + 1))
}

pass() {
  echo "  ✓ $1"
}

git_quiet() {
  git -c user.name=test -c user.email=test@example.com "$@" >/dev/null 2>&1
}

# master 上有兩份報告與一個無關檔；feature 從 master 分出並有一個無關 commit，之後 master 再前移一次，
# 讓 merge-base 不等於任何分支頭，才驗得出 BASELINE 是不是真的 merge-base。
new_repo() {
  local repo="$WORK_DIR/$1"
  mkdir -p "$repo/docs"
  git_quiet -C "$repo" init -b master
  echo "a" > "$repo/$REPORT_A"
  echo "b" > "$repo/$REPORT_B"
  echo "x" > "$repo/other.txt"
  git_quiet -C "$repo" add -A
  git_quiet -C "$repo" commit -m base
  git_quiet -C "$repo" checkout -b feature
  echo "feature" >> "$repo/other.txt"
  git_quiet -C "$repo" commit -am "feature work"
  git_quiet -C "$repo" checkout master
  echo "master" > "$repo/master-only.txt"
  git_quiet -C "$repo" add -A
  git_quiet -C "$repo" commit -m "master moves"
  git_quiet -C "$repo" checkout feature
  echo "$repo"
}

run_checker() {
  local repo="$1"; shift
  (cd "$repo" && bash "$CHECKER" "$@" --baseline-file baseline.txt --report "$REPORT_A" --report "$REPORT_B" 2>&1)
}

# $1 名稱、$2 預期（0 或 fail）、$3 repo，其餘為子命令
expect() {
  local name="$1" expected="$2" repo="$3"; shift 3
  local output status
  output="$(run_checker "$repo" "$@")"
  status=$?
  if { [ "$expected" = "0" ] && [ "$status" -eq 0 ]; } || { [ "$expected" != "0" ] && [ "$status" -ne 0 ]; }; then
    pass "$name"
  else
    fail "$name (exit $status): $output"
  fi
}

field() {
  sed -n "s/^$2=//p" "$1/baseline.txt"
}

# 以 record 建立基準檔後，用 $2 這段 sed 改寫基準檔，再驗證 verify 失敗。
expect_verify_fails_after_edit() {
  local name="$1" sed_expr="$2" repo
  repo="$(new_repo "edit-$RANDOM-$RANDOM")"
  run_checker "$repo" record >/dev/null
  sed -i "$sed_expr" "$repo/baseline.txt"
  expect "$name" fail "$repo" verify
}

echo "record"
repo="$(new_repo record)"
expect "record succeeds" 0 "$repo" record
master_sha="$(git -C "$repo" rev-parse master)"
head_sha="$(git -C "$repo" rev-parse HEAD)"
merge_base="$(git -C "$repo" merge-base master HEAD)"
[ "$(field "$repo" MASTER_SHA)" = "$master_sha" ] && pass "MASTER_SHA is master at record time" || fail "MASTER_SHA: $(field "$repo" MASTER_SHA) != $master_sha"
[ "$(field "$repo" START_HEAD)" = "$head_sha" ] && pass "START_HEAD is HEAD at record time" || fail "START_HEAD: $(field "$repo" START_HEAD) != $head_sha"
[ "$(field "$repo" BASELINE)" = "$merge_base" ] && pass "BASELINE is merge-base of MASTER_SHA and START_HEAD" || fail "BASELINE: $(field "$repo" BASELINE) != $merge_base"
[ "$merge_base" != "$master_sha" ] && [ "$merge_base" != "$head_sha" ] && pass "fixture merge-base differs from both heads" || fail "fixture merge-base equals a branch head"
expect "record refuses to overwrite an existing baseline" fail "$repo" record

echo "verify: success output"
output="$(run_checker "$repo" verify)"; status=$?
[ "$status" -eq 0 ] && pass "verify passes on untouched reports" || fail "verify on untouched reports (exit $status): $output"
for key in MASTER_SHA BASELINE START_HEAD; do
  grep -qx "$key=$(field "$repo" "$key")" <<< "$output" && pass "output lists $key from baseline file" || fail "output missing $key=$(field "$repo" "$key"): $output"
done
grep -qx "HEAD=$head_sha" <<< "$output" && pass "output lists current HEAD" || fail "output missing HEAD=$head_sha: $output"

echo "verify: baseline file contract"
repo="$(new_repo missing-file)"
expect "missing baseline file fails" fail "$repo" verify
for key in MASTER_SHA BASELINE START_HEAD; do
  expect_verify_fails_after_edit "missing $key fails" "/^$key=/d"
  expect_verify_fails_after_edit "unknown $key commit fails" "s/^$key=.*/$key=0123456789abcdef0123456789abcdef01234567/"
  expect_verify_fails_after_edit "empty $key fails" "s/^$key=.*/$key=/"
done
# 名稱（master、HEAD）不是固定的 SHA；verify 不得解析它們，否則等同重新解析 master。
expect_verify_fails_after_edit "branch name instead of SHA fails" "s/^MASTER_SHA=.*/MASTER_SHA=master/"
expect_verify_fails_after_edit "abbreviated SHA fails" "s/^\(BASELINE=.\{7\}\).*/\1/"
repo="$(new_repo wrong-baseline)"
run_checker "$repo" record >/dev/null
sed -i "s/^BASELINE=.*/BASELINE=$(git -C "$repo" rev-parse master)/" "$repo/baseline.txt"
expect "BASELINE that is not the merge-base fails" fail "$repo" verify

echo "verify: master moves after record"
repo="$(new_repo master-moves)"
run_checker "$repo" record >/dev/null
saved_master="$(field "$repo" MASTER_SHA)"
git_quiet -C "$repo" checkout master
echo "changed on master" >> "$repo/$REPORT_A"
git_quiet -C "$repo" commit -am "master edits report after record"
git_quiet -C "$repo" checkout feature
output="$(run_checker "$repo" verify)"; status=$?
[ "$status" -eq 0 ] && grep -qx "MASTER_SHA=$saved_master" <<< "$output" \
  && pass "verify uses saved MASTER_SHA after master moves" || fail "verify after master moved (exit $status): $output"
git_quiet -C "$repo" branch -D master
expect "verify passes without a master branch" 0 "$repo" verify

echo "verify: report changes"
for report in "$REPORT_A" "$REPORT_B"; do
  repo="$(new_repo "commit-$RANDOM")"
  run_checker "$repo" record >/dev/null
  echo "edited" >> "$repo/$report"
  git_quiet -C "$repo" commit -am "edit report"
  expect "commit touching $report fails" fail "$repo" verify

  repo="$(new_repo "revert-$RANDOM")"
  run_checker "$repo" record >/dev/null
  cp "$repo/$report" "$WORK_DIR/original"
  echo "edited" >> "$repo/$report"
  git_quiet -C "$repo" commit -am "edit report"
  cp "$WORK_DIR/original" "$repo/$report"
  git_quiet -C "$repo" commit -am "restore report"
  expect "commit touching $report then restoring it fails" fail "$repo" verify

  repo="$(new_repo "worktree-$RANDOM")"
  run_checker "$repo" record >/dev/null
  echo "edited" >> "$repo/$report"
  expect "working-tree change to $report fails" fail "$repo" verify

  repo="$(new_repo "index-$RANDOM")"
  run_checker "$repo" record >/dev/null
  echo "edited" >> "$repo/$report"
  git_quiet -C "$repo" add "$report"
  # 工作樹與 index 相同，只有 index 對 HEAD 有差異，才單獨驗到 --cached 檢查。
  expect "staged change to $report fails" fail "$repo" verify

  # side 分支改後又復原，再 --no-ff 合併：預設的歷史簡化會走 TREESAME 的父節點而略過，需 --full-history 才看得到。
  repo="$(new_repo "merge-$RANDOM")"
  run_checker "$repo" record >/dev/null
  git_quiet -C "$repo" checkout -b side
  cp "$repo/$report" "$WORK_DIR/original"
  echo "edited" >> "$repo/$report"
  git_quiet -C "$repo" commit -am "edit report on side"
  cp "$WORK_DIR/original" "$repo/$report"
  git_quiet -C "$repo" commit -am "restore report on side"
  git_quiet -C "$repo" checkout feature
  git_quiet -C "$repo" merge --no-ff side -m "merge side"
  expect "merged side branch that touched $report then restored it fails" fail "$repo" verify

  for flag in --skip-worktree --assume-unchanged; do
    repo="$(new_repo "flag-$RANDOM")"
    run_checker "$repo" record >/dev/null
    git -C "$repo" update-index "$flag" "$report"
    echo "edited" >> "$repo/$report"
    expect "working-tree change hidden by $flag on $report fails" fail "$repo" verify
  done
done
repo="$(new_repo wrong-path)"
run_checker "$repo" record >/dev/null
output="$(cd "$repo" && bash "$CHECKER" verify --baseline-file baseline.txt --report docs/no-such-report.md 2>&1)"; status=$?
[ "$status" -ne 0 ] && pass "report path that does not exist fails" || fail "nonexistent report path passed: $output"
repo="$(new_repo unrelated)"
run_checker "$repo" record >/dev/null
echo "more" >> "$repo/other.txt"
git_quiet -C "$repo" commit -am "unrelated change"
expect "commit touching other files passes" 0 "$repo" verify

if [ "$FAILURES" -eq 0 ]; then echo "all cases passed"; else echo "$FAILURES failure(s)"; exit 1; fi
