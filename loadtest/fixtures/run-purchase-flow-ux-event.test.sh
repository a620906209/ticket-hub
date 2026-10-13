#!/usr/bin/env bash
# run-purchase-flow-ux-event.sh 的自動化測試（tasks.md 1.0「並行保護」）。
# 以 PATH 上的假 docker 模擬 redis 與 k6 容器，不碰真實 redis、API 與 [PurchaseFlowUx] 保留額度，
# 所以可以反覆執行，也能重現中斷、redis 中斷這類難以在真實環境觸發的情境。
# 用法：bash loadtest/fixtures/run-purchase-flow-ux-event.test.sh；任一案例失敗即以 1 結束。
set -uo pipefail

WRAPPER="$(cd "$(dirname "$0")" && pwd)/run-purchase-flow-ux-event.sh"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
mkdir -p "$WORK_DIR/bin"

# 假 docker：鎖存在 $FAKE_STATE/lock，k6 容器存在與否以 $FAKE_STATE/container 表示。
cat > "$WORK_DIR/bin/docker" <<'FAKE'
#!/usr/bin/env bash
state="$FAKE_STATE"
echo "$*" >> "$state/calls"
case "$*" in
  "compose exec -T redis redis-cli "*)
    [ -f "$state/redis-down" ] && exit 1
    set -- "${@:6}"
    case "$1" in
      SET) if [ -f "$state/lock" ]; then echo ""; else printf '%s' "$3" > "$state/lock"; echo OK; fi ;;
      GET) [ -f "$state/lock" ] && cat "$state/lock"; echo ;;
      EVAL) [ -f "$state/eval-fails" ] && exit 1
        if [ -f "$state/lock" ] && [ "$(cat "$state/lock")" = "$5" ]; then rm -f "$state/lock"; echo 1; else echo 0; fi ;;
    esac
    ;;
  "compose --profile loadtest run "*)
    token="$(printf '%s\n' "$@" | sed -n 's/^PFUX_FIXTURE_LOCK_TOKEN=//p')"
    printf "%s" "$token" > "$state/k6-token"
    printf '%s\n' "$@" | sed -n '/^--name$/{n;p;}' > "$state/container-name"
    touch "$state/container"
    case "${FAKE_K6_MODE:-exit0}" in
      exit0) rm -f "$state/container"; exit 0 ;;
      exit107) rm -f "$state/container"; exit 107 ;;
      lose-lock) rm -f "$state/lock" "$state/container"; exit 0 ;;
      redis-down-after) touch "$state/redis-down"; rm -f "$state/container"; exit 0 ;;
      # 模擬 compose 用戶端收到中斷就結束、容器卻仍在執行：容器標記不刪除。
      # FAKE_LATE_CONTAINER：用戶端結束時容器還沒建好（第一次查詢看不到），稍後才出現。
      hang)
        # 背景的 sleep 不會收到 SIGINT，結束前必須自己 kill，否則會留下孤兒程序。
        trap 'kill "$sleep_pid" 2>/dev/null; if [ -n "${FAKE_LATE_CONTAINER:-}" ]; then rm -f "$state/container"; (sleep 1; touch "$state/container" "$state/late-created") & fi; exit 130' INT
        # 先取得 sleep_pid 再發出 k6-started：測試一看到 k6-started 就送訊號，順序相反時 trap 會 kill 空 PID。
        sleep "${FAKE_K6_SLEEP:-30}" & sleep_pid=$!
        touch "$state/k6-started"; wait "$sleep_pid"
        rm -f "$state/container"; exit 0 ;;
    esac
    ;;
  "ps -aq --filter "*) [ -f "$state/container" ] && [ "$4" = "name=^$(cat "$state/container-name")\$" ] && echo fakeid ;;
  "rm -f "*) [ -f "$state/rm-fails" ] || { [ "$3" = "$(cat "$state/container-name")" ] && rm -f "$state/container"; } ;;
esac
exit 0
FAKE
chmod +x "$WORK_DIR/bin/docker"

failures=0
case_no=0

new_state() {
  case_no=$((case_no + 1))
  FAKE_STATE="$WORK_DIR/state-$case_no"
  mkdir -p "$FAKE_STATE"
  export FAKE_STATE
}

run_wrapper() {
  PATH="$WORK_DIR/bin:$PATH" bash "$WRAPPER" > "$FAKE_STATE/out" 2>&1
}

check() {
  local name="$1" condition="$2"
  if eval "$condition"; then
    echo "PASS $name"
  else
    echo "FAIL $name ($condition)"
    sed 's/^/    /' "$FAKE_STATE/out" 2>/dev/null
    failures=$((failures + 1))
  fi
}

# 已有別人的鎖時不得執行 k6，也不得刪除別人的鎖，否則兩個 fixture 會同時計數。
new_state; printf 'other-holder' > "$FAKE_STATE/lock"
FAKE_K6_MODE=exit0 run_wrapper; code=$?
check "Run_WhenLockHeldByOther_Exits3WithoutRunningK6" '[ $code -eq 3 ] && grep -q LOCK_BUSY "$FAKE_STATE/out" && [ ! -f "$FAKE_STATE/k6-token" ] && [ "$(cat "$FAKE_STATE/lock")" = other-holder ]'

# redis 無法使用時要與「鎖忙碌」區分，否則使用者會誤以為有人在跑。
new_state; touch "$FAKE_STATE/redis-down"
run_wrapper; code=$?
check "Run_WhenRedisUnavailable_Exits3WithLockFailed" '[ $code -eq 3 ] && grep -q LOCK_FAILED "$FAKE_STATE/out" && ! grep -q LOCK_BUSY "$FAKE_STATE/out" && [ ! -f "$FAKE_STATE/k6-token" ]'

# 成功路徑：fixture 收到符合格式的 token，結束後鎖要釋放，否則下一次執行會被擋 300 秒。
new_state
FAKE_K6_MODE=exit0 run_wrapper; code=$?
check "Run_WhenK6Succeeds_PassesTokenAndReleasesLock" '[ $code -eq 0 ] && grep -Eq "^[0-9a-f]{32}$" "$FAKE_STATE/k6-token" && [ ! -f "$FAKE_STATE/lock" ]'

# k6 失敗時結束碼要原樣傳回，鎖同樣要釋放。
new_state
FAKE_K6_MODE=exit107 run_wrapper; code=$?
check "Run_WhenK6Fails_ReturnsK6ExitAndReleasesLock" '[ $code -eq 107 ] && [ ! -f "$FAKE_STATE/lock" ]'

# 鎖在執行期間過期時，即使 k6 成功也不得視為通過，並保留 k6 結束碼供判讀。
new_state
FAKE_K6_MODE=lose-lock run_wrapper; code=$?
check "Run_WhenLockLostDuringRun_Exits4" '[ $code -eq 4 ] && grep -q "LOCK_LOST.*k6 exit 0" "$FAKE_STATE/out"'

# k6 結束後 redis 不可用時無法確認互斥，必須以 4 結束且訊息不得誤稱鎖過期。
new_state
FAKE_K6_MODE=redis-down-after run_wrapper; code=$?
check "Run_WhenRedisDownAfterK6_Exits4WithCheckFailed" '[ $code -eq 4 ] && grep -q "LOCK_CHECK_FAILED.*k6 exit 0" "$FAKE_STATE/out" && ! grep -q LOCK_LOST "$FAKE_STATE/out"'

# 中斷 wrapper（模擬 Ctrl-C 送給整個 process group）時，k6 容器仍在跑；
# 必須先移除容器再釋放鎖，否則下一個 fixture 會在這個容器仍在建立資料時通過上限檢查。
# 直接在背景啟動 wrapper（不經函式），$! 才是 wrapper 本身，SIGTERM 案例才能只送給它。
start_hanging_wrapper() {
  set -m
  PATH="$WORK_DIR/bin:$PATH" FAKE_K6_MODE=hang bash "$WRAPPER" > "$FAKE_STATE/out" 2>&1 &
  wrapper_pid=$!
  set +m
  for _ in $(seq 1 100); do [ -f "$FAKE_STATE/k6-started" ] && return 0; sleep 0.1; done
  echo "FAIL case $case_no: fake k6 did not start within 10s" >&2
  failures=$((failures + 1))
}

interrupt_wrapper() {
  start_hanging_wrapper
  kill -INT -- "-$wrapper_pid"
  wait "$wrapper_pid"
}

# ps 與 rm 必須用 run 時的同一個容器名稱，否則會找不到仍在執行的容器而提早釋放鎖。
removed_registered_container='grep -qx "rm -f $(cat "$FAKE_STATE/container-name")" "$FAKE_STATE/calls"'
released_after_rm='[ "$(grep -n "^rm -f" "$FAKE_STATE/calls" | tail -1 | cut -d: -f1)" -lt "$(grep -n "redis-cli EVAL" "$FAKE_STATE/calls" | cut -d: -f1)" ]'

new_state
interrupt_wrapper; code=$?
check "Run_WhenInterrupted_RemovesContainerBeforeReleasingLock" '[ $code -eq 130 ] && grep -Eqx "pfux-fixture-[0-9a-f]{32}" "$FAKE_STATE/container-name" && '"$removed_registered_container"' && [ ! -f "$FAKE_STATE/container" ] && [ ! -f "$FAKE_STATE/lock" ] && '"$released_after_rm"

# 中斷時容器還在建立、第一次查詢看不到；若立即釋放鎖，稍後出現的容器會與下一個 fixture 並行。
new_state
FAKE_LATE_CONTAINER=1 interrupt_wrapper; code=$?
# 等假 docker 的延遲建立子程序做完，避免它在下一個案例或測試結束後仍存活；也證明容器確實晚出現過。
for _ in $(seq 1 50); do [ -f "$FAKE_STATE/late-created" ] && break; sleep 0.1; done
check "Run_WhenInterruptedBeforeContainerVisible_RechecksAndRemovesIt" '[ $code -eq 130 ] && [ -f "$FAKE_STATE/late-created" ] && '"$removed_registered_container"' && [ ! -f "$FAKE_STATE/container" ] && [ ! -f "$FAKE_STATE/lock" ] && '"$released_after_rm"

# 容器移除失敗時不得釋放鎖，交給 TTL；寧可擋住下一次執行也不讓兩個 fixture 同時計數。
new_state; touch "$FAKE_STATE/rm-fails"
interrupt_wrapper; code=$?
check "Run_WhenInterruptedAndContainerCannotBeRemoved_KeepsLock" '[ $code -eq 130 ] && grep -q LOCK_KEPT "$FAKE_STATE/out" && [ -f "$FAKE_STATE/lock" ] && ! grep -q "redis-cli EVAL" "$FAKE_STATE/calls"'

# 只對 wrapper 送 SIGTERM 時 bash 會等 k6 跑完；這段期間鎖必須一直持有，跑完後才以 143 結束並釋放。
new_state
FAKE_K6_SLEEP=3 start_hanging_wrapper
kill -TERM "$wrapper_pid"; sleep 1
held_during_k6=no; [ -f "$FAKE_STATE/lock" ] && [ -f "$FAKE_STATE/container" ] && held_during_k6=yes
wait "$wrapper_pid"; code=$?
check "Run_WhenTerminatedDuringK6_HoldsLockUntilK6Finishes" '[ $held_during_k6 = yes ] && [ $code -eq 143 ] && [ ! -f "$FAKE_STATE/container" ] && [ ! -f "$FAKE_STATE/lock" ]'

# 關閉終端機（SIGHUP）比照 SIGTERM：k6 跑完前一直持有鎖，並視為中斷，釋放前重查容器（ps 呼叫 2 次）。
new_state
FAKE_K6_SLEEP=3 start_hanging_wrapper
kill -HUP "$wrapper_pid"; sleep 1
held_during_k6=no; [ -f "$FAKE_STATE/lock" ] && [ -f "$FAKE_STATE/container" ] && held_during_k6=yes
wait "$wrapper_pid"; code=$?
check "Run_WhenHangupDuringK6_HoldsLockAndRechecksContainer" '[ $held_during_k6 = yes ] && [ $code -eq 129 ] && [ "$(grep -c "^ps -aq" "$FAKE_STATE/calls")" -eq 2 ] && [ ! -f "$FAKE_STATE/container" ] && [ ! -f "$FAKE_STATE/lock" ]'

# 釋放鎖失敗時若沒有訊息，使用者只會在下一次執行看到 LOCK_BUSY 而不知原因；結束碼仍須是 k6 的結果。
new_state; touch "$FAKE_STATE/eval-fails"
FAKE_K6_MODE=exit0 run_wrapper; code=$?
check "Run_WhenLockReleaseFails_WarnsAndKeepsK6Exit" '[ $code -eq 0 ] && grep -q LOCK_RELEASE_FAILED "$FAKE_STATE/out" && [ -f "$FAKE_STATE/lock" ]'

echo "cases=$case_no failures=$failures"
[ "$failures" -eq 0 ]
