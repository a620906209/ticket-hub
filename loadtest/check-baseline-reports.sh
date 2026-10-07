#!/usr/bin/env bash
# 第一階段報告未修改的證明（order-placement-p95-phase2，LT-REPORT-007 基準檔契約）。非 0 結束代表不可作為驗收證據。
#
# 用法：
#   bash loadtest/check-baseline-reports.sh record [選項]   正式驗收開始前執行一次，固定基準
#   bash loadtest/check-baseline-reports.sh verify [選項]   判定時執行
# 選項（供測試覆寫；相對路徑一律以 repo 根目錄為準，不是目前目錄）：
#   --baseline-file <路徑>  預設 loadtest/.output/p95-phase2-baseline.txt（相對 repo 根目錄）
#   --report <路徑>         可重複；預設 docs/load-test/report.md 與 docs/load-test/p95-optimization-report.md
#
# record：寫入 MASTER_SHA（當下 master）、START_HEAD（當下 HEAD）、BASELINE（兩者的 merge-base）。基準檔已存在時拒絕覆寫，
#         避免驗收途中 master 前移後重新 record，讓基準跟著漂移。
# verify：只讀基準檔，不解析 master（master 可能已前移或含報告修改）；三個值都必須是完整 40 碼且存在的 commit，
#         BASELINE 必須等於 MASTER_SHA 與 START_HEAD 的 merge-base；BASELINE..HEAD 之間不得有 commit 觸及報告
#         （逐筆列 commit，改後又復原也算），工作樹與 index 也不得修改報告。
set -euo pipefail

usage() {
  echo "usage: $0 record|verify [--baseline-file <path>] [--report <path>]..." >&2
  exit 2
}

[ "$#" -ge 1 ] || usage
COMMAND="$1"; shift
case "$COMMAND" in record|verify) ;; *) usage ;; esac

cd "$(git rev-parse --show-toplevel)"
BASELINE_FILE=loadtest/.output/p95-phase2-baseline.txt
REPORTS=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --baseline-file) [ "$#" -ge 2 ] || usage; BASELINE_FILE="$2"; shift 2 ;;
    --report) [ "$#" -ge 2 ] || usage; REPORTS+=("$2"); shift 2 ;;
    *) usage ;;
  esac
done
[ "${#REPORTS[@]}" -gt 0 ] || REPORTS=(docs/load-test/report.md docs/load-test/p95-optimization-report.md)

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

record() {
  [ -e "$BASELINE_FILE" ] && fail "$BASELINE_FILE already exists; the baseline must not be re-recorded"
  local master_sha start_head baseline
  master_sha="$(git rev-parse --verify 'master^{commit}')"
  start_head="$(git rev-parse --verify 'HEAD^{commit}')"
  baseline="$(git merge-base "$master_sha" "$start_head")"
  mkdir -p "$(dirname "$BASELINE_FILE")"
  printf 'MASTER_SHA=%s\nBASELINE=%s\nSTART_HEAD=%s\n' "$master_sha" "$baseline" "$start_head" > "$BASELINE_FILE"
  cat "$BASELINE_FILE"
}

# 只接受完整 SHA：分支名或縮寫會被 git 解析成「當下」的 commit，等於繞過固定的基準。
read_sha() {
  local key="$1" value
  value="$(sed -n "s/^$key=//p" "$BASELINE_FILE" | tr -d '\r')"
  [ "$(grep -c "^$key=" "$BASELINE_FILE")" -eq 1 ] || fail "$key must appear exactly once in $BASELINE_FILE"
  [[ "$value" =~ ^[0-9a-f]{40}$ ]] || fail "$key is not a full commit SHA: '$value'"
  git cat-file -e "$value^{commit}" 2>/dev/null || fail "$key commit does not exist: $value"
  echo "$value"
}

verify() {
  [ -f "$BASELINE_FILE" ] || fail "$BASELINE_FILE not found; run record before the formal acceptance runs"
  local master_sha baseline start_head head touched
  master_sha="$(read_sha MASTER_SHA)"
  baseline="$(read_sha BASELINE)"
  start_head="$(read_sha START_HEAD)"
  head="$(git rev-parse --verify 'HEAD^{commit}')"

  [ "$(git merge-base "$master_sha" "$start_head")" = "$baseline" ] \
    || fail "BASELINE $baseline is not the merge-base of MASTER_SHA and START_HEAD"
  git merge-base --is-ancestor "$baseline" "$head" || fail "BASELINE $baseline is not an ancestor of HEAD $head"

  # spec 不要求，但 HEAD 若不是 START_HEAD 的後代，判定時的程式碼可能與量測時不同，提醒人工確認。
  git merge-base --is-ancestor "$start_head" "$head" \
    || echo "WARNING: HEAD $head is not a descendant of START_HEAD $start_head; the measured code may differ" >&2

  local report
  for report in "${REPORTS[@]}"; do
    git cat-file -e "$baseline:$report" 2>/dev/null || fail "$report does not exist at BASELINE (wrong --report path?)"
    git cat-file -e "$head:$report" 2>/dev/null || fail "$report does not exist at HEAD"
    # skip-worktree／assume-unchanged 會讓 git diff 看不到工作樹的修改。
    # ls-files -v：S 為 skip-worktree，小寫字母為 assume-unchanged。
    case "$(git ls-files -v -- "$report")" in
      S\ *|[a-z]\ *) fail "$report has skip-worktree or assume-unchanged set; working-tree changes would be hidden" ;;
    esac
  done

  # --full-history：side 分支上改後又復原、再合併進來時，預設的歷史簡化會沿著 TREESAME 的父節點走而略過那些 commit。
  touched="$(git log --full-history --format='%H %s' "$baseline..$head" -- "${REPORTS[@]}")"
  [ -z "$touched" ] || fail "commits after BASELINE touch the phase 1 reports:"$'\n'"$touched"
  git diff --quiet -- "${REPORTS[@]}" || fail "working tree modifies the phase 1 reports"
  git diff --cached --quiet -- "${REPORTS[@]}" || fail "index modifies the phase 1 reports"

  echo "MASTER_SHA=$master_sha"
  echo "BASELINE=$baseline"
  echo "START_HEAD=$start_head"
  echo "HEAD=$head"
  echo "OK: phase 1 reports unchanged since BASELINE (${REPORTS[*]})"
}

"$COMMAND"
