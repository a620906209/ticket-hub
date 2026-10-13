#!/usr/bin/env bash
# purchase-flow-ux fixture 的執行入口（openspec/changes/purchase-flow-ux tasks.md 1.0「並行保護」）。
# fixture 先計數 [PurchaseFlowUx] 活動再建立資料，兩步之間不是原子操作；兩個 fixture 並行時都可能通過
# 上限檢查而突破保留上限。這裡在 compose 的 redis 上取得互斥鎖，所有主機與容器共用同一個 redis，
# 所以鎖一定共用；k6 無法連 Redis，只能由 wrapper 取鎖，再把鎖 token 傳給 fixture。
#
# 用法：bash loadtest/fixtures/run-purchase-flow-ux-event.sh（先依 tasks.md 1.0 跑 seeder 並輪詢 api 就緒）
# 結束碼：k6 的結束碼；取鎖失敗為 3；鎖在執行期間遺失或無法確認為 4（此時即使 k6 成功也不得視為通過）。
set -uo pipefail

LOCK_KEY='loadtest:purchase-flow-ux:fixture-lock'
# 必須大於一次 fixture 的最長時間：k6 setupTimeout 120s，加上容器啟動。當掉時由 TTL 自動釋放。
LOCK_TTL_SECONDS=300
# 只在值仍是自己的 token 時才刪除，避免鎖過期後誤刪下一個執行者的鎖。
RELEASE_SCRIPT='if redis.call("GET", KEYS[1]) == ARGV[1] then return redis.call("DEL", KEYS[1]) else return 0 end'

cd "$(dirname "$0")/../.." || exit 2

redis_cli() {
  docker compose exec -T redis redis-cli "$@"
}

lock_token="$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')"
if [ -z "$lock_token" ]; then
  echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_FAILED: cannot generate lock token" >&2
  exit 3
fi
# 中斷時要能以名稱找到並移除 k6 容器，確認它停止後才釋放鎖。
k6_container="pfux-fixture-${lock_token}"

if ! acquire_result="$(redis_cli SET "$LOCK_KEY" "$lock_token" NX EX "$LOCK_TTL_SECONDS")"; then
  echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_FAILED: redis is unavailable, cannot acquire the lock" >&2
  exit 3
fi
if [ "$acquire_result" != "OK" ]; then
  echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_BUSY: another fixture run holds the lock; do not run fixtures in parallel" >&2
  exit 3
fi

# 中斷時 compose 用戶端已結束，但 daemon 可能仍在建立容器，第一次查詢看不到；等這段時間後再查一次。
LATE_CONTAINER_RECHECK_SECONDS=2
was_interrupted=0

# 容器不存在（或已移除）時回傳 0；無法確認時回傳 1。
remove_k6_container() {
  local remaining
  remaining="$(docker ps -aq --filter "name=^${k6_container}\$")" || return 1
  [ -z "$remaining" ] && return 0
  docker rm -f "$k6_container" > /dev/null
  remaining="$(docker ps -aq --filter "name=^${k6_container}\$")" || return 1
  [ -z "$remaining" ]
}

# 中斷（Ctrl-C）時 k6 容器可能仍在執行；先移除容器並確認已不存在才釋放鎖，
# 無法確認時保留鎖，交給 TTL 釋放，寧可擋住下一次執行也不讓兩個 fixture 同時計數。
release_lock_after_k6_stopped() {
  if remove_k6_container && { [ "$was_interrupted" -eq 0 ] || { sleep "$LATE_CONTAINER_RECHECK_SECONDS"; remove_k6_container; }; }; then
    # 釋放失敗不改變結束碼（fixture 結果已確定），但要讓使用者知道鎖仍在，下一次執行會得到 LOCK_BUSY 直到 TTL 到期。
    if ! redis_cli EVAL "$RELEASE_SCRIPT" 1 "$LOCK_KEY" "$lock_token" > /dev/null; then
      echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_RELEASE_FAILED: could not release lock; it will expire after ${LOCK_TTL_SECONDS}s" >&2
    fi
    return
  fi
  echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_KEPT: cannot confirm k6 container ${k6_container} has stopped; lock is left to expire after ${LOCK_TTL_SECONDS}s" >&2
}
trap release_lock_after_k6_stopped EXIT
# bash 會等前景的 docker compose run 結束才執行這些 trap；只對 wrapper 送 SIGTERM／SIGHUP 不會中止 k6，
# 只會在 k6 跑完後以 143／129 結束，期間鎖一直持有。
trap 'was_interrupted=1; exit 130' INT
trap 'was_interrupted=1; exit 143' TERM
trap 'was_interrupted=1; exit 129' HUP

MSYS_NO_PATHCONV=1 docker compose --profile loadtest run --rm --no-deps --name "$k6_container" k6 run \
  -e PFUX_FIXTURE_LOCK_TOKEN="$lock_token" /scripts/fixtures/purchase-flow-ux-event.js
k6_exit=$?

if ! lock_holder="$(redis_cli GET "$LOCK_KEY")"; then
  echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_CHECK_FAILED: redis is unavailable, cannot confirm the lock was held for the whole run (k6 exit ${k6_exit}); check the [PurchaseFlowUx] event count" >&2
  exit 4
fi
if [ "$lock_holder" != "$lock_token" ]; then
  echo "PURCHASE_FLOW_UX_FIXTURE_LOCK_LOST: lock expired during the run (k6 exit ${k6_exit}); retention limit is not guaranteed, check the [PurchaseFlowUx] event count" >&2
  exit 4
fi
exit "$k6_exit"
