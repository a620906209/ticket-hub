#!/usr/bin/env bash
# k8s manifest 的替代驗證（k8s-local-manifests design.md 決策 8）：把 k8s/ 以獨立 Namespace
# projectc-verify 套用到 Docker Desktop 叢集，逐項斷言；任一 FAIL 即非 0 結束。
# 用法：bash scripts/verify-k8s-manifests.sh
#   KEEP_UP=1  驗證後保留 projectc-verify（供真實瀏覽器檢查，NodePort 30081）；
#              用完請自行 `kubectl --context docker-desktop delete namespace projectc-verify`。
# 前置：已以 docker build 建好 projectc/api:local、projectc/api-migrator:local、projectc/web:local
#   （指令見 deploy/api.Dockerfile 與 deploy/web.Dockerfile 開頭註解）；本機需有 kubectl、jq、curl、docker。
# 結束碼：0 全 PASS；1 有 FAIL；2 前置條件不符（context 不對、缺工具或映像）；
#   3 無 FAIL 但有 UNVERIFIED（本機 CNI 不執行 NetworkPolicy 時的預期結果）。
# 機敏值於執行時隨機產生並只寫入 mktemp 暫存目錄（trap 刪除），不寫入 repo；不使用 set -x。
# 只透過 kubectl／docker 操作，不在本機執行 dotnet／psql。
set -uo pipefail
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

cd "$(dirname "$0")/.."

readonly CONTEXT="docker-desktop"
readonly NS="projectc-verify"
readonly NP_NS="projectc-verify-np"
readonly VERIFY_NODEPORT=30081
readonly K8S_DIR="k8s"
readonly MIGRATIONS_DIR="src/ProjectC.Infrastructure/Persistence/Migrations"
readonly BASE="http://localhost:${VERIFY_NODEPORT}"
readonly SECRET_KEYS=(POSTGRES_PASSWORD ConnectionStrings__DefaultConnection Jwt__SigningKey TicketSigning__SigningKey DevelopmentSeed__AdminPassword)
readonly MANIFESTS=(00-namespace 10-configmap 20-postgres 21-redis 22-seq 30-migrator-job 40-api 50-web 60-networkpolicy)

k() { kubectl --context "$CONTEXT" "$@"; }
kn() { k -n "$NS" "$@"; }

passed=0
failed=0
unverified=0
unverified_items=()

pass() { echo "PASS [$1] $2"; passed=$((passed + 1)); }
fail() {
  echo "FAIL [$1] $2"
  if [ -n "${3:-}" ]; then echo "---- 實際輸出 ----"; echo "$3" | tail -n 40; echo "------------------"; fi
  failed=$((failed + 1))
}
mark_unverified() {
  echo "UNVERIFIED [$1] $2"
  unverified=$((unverified + 1))
  unverified_items+=("$1")
}
finish() {
  echo "SUMMARY: ${passed} passed, ${failed} failed, ${unverified} unverified"
  if [ "$unverified" -gt 0 ]; then echo "UNVERIFIED: ${unverified_items[*]}"; fi
  if [ "$failed" -gt 0 ]; then exit 1; fi
  if [ "$unverified" -gt 0 ]; then exit 3; fi
  exit 0
}

# ---- 前置條件：context 守衛（斷言以外的硬性條件，不符就不碰叢集） ----
current_context="$(kubectl config current-context 2>&1)"
if [ "$current_context" != "$CONTEXT" ]; then
  echo "拒絕執行：目前 context 是 '$current_context'，只允許 '$CONTEXT'"
  exit 2
fi
for tool in jq curl docker; do
  command -v "$tool" >/dev/null 2>&1 || { echo "缺少工具：$tool"; exit 2; }
done

# ---- 斷言 0：manifest 與映像存在（尚未建立 manifest 時在此失敗，不動叢集） ----
missing=0
for name in "${MANIFESTS[@]}"; do
  [ -f "$K8S_DIR/$name.yaml" ] || { echo "缺少 $K8S_DIR/$name.yaml"; missing=1; }
done
[ -f "$K8S_DIR/secret.example.yaml" ] || { echo "缺少 $K8S_DIR/secret.example.yaml"; missing=1; }
if [ "$missing" -ne 0 ]; then
  fail 0 "manifest 不完整，無法驗證（未對叢集做任何操作）"
  finish
fi
for image in projectc/api:local projectc/api-migrator:local projectc/web:local; do
  docker image inspect "$image" >/dev/null 2>&1 || { echo "缺少映像 $image，請先 docker build"; exit 2; }
done

random_hex() { od -An -N"$1" -tx1 /dev/urandom | tr -d ' \n'; }

work_dir="$(mktemp -d)"
# Windows 原生 kubectl 讀不到 MSYS 的 /tmp 路徑，需轉成原生路徑；沒有 cygpath 的環境（Linux／macOS）原樣使用。
if command -v cygpath >/dev/null 2>&1; then work_native="$(cygpath -m "$work_dir")"; else work_native="$work_dir"; fi
bg_pids=()

stop_background() {
  for pid in "${bg_pids[@]:-}"; do
    [ -n "$pid" ] && kill "$pid" >/dev/null 2>&1
  done
  bg_pids=()
}
# 所有權標記：腳本只清理自己建立（帶此 label）的 Namespace，避免刪到同名的既有資源。
readonly OWNER_LABEL="verify-k8s-manifests/owned=true"
owns_namespace() { [ "$(k get namespace "$1" -o jsonpath='{.metadata.labels.verify-k8s-manifests/owned}' 2>/dev/null)" = "true" ]; }
# 互斥鎖：固定 Namespace 與 NodePort 無法並行；另一個執行個體仍存活時拒絕執行，避免把它的 Namespace 當殘留刪掉。
lock_dir="${TMPDIR:-/tmp}/verify-k8s-manifests.lock"
holds_lock=0
# 只用原子的 mkdir 取得鎖，不自動接管殘留的鎖（接管本身無法原子完成）；程序被 SIGKILL 才會留下殘鎖，需人工刪除。
if ! mkdir "$lock_dir" 2>/dev/null; then
  lock_pid="$(cat "$lock_dir/pid" 2>/dev/null)"
  echo "PRECONDITION: 互斥鎖 $lock_dir 已存在（持有者 pid ${lock_pid:-未知}）。若該程序已不存在，請手動刪除此目錄後重試。" >&2
  exit 2
fi
echo "$$" > "$lock_dir/pid"
holds_lock=1
for existing_ns in "$NS" "$NP_NS"; do
  if k get namespace "$existing_ns" >/dev/null 2>&1 && ! owns_namespace "$existing_ns"; then
    echo "PRECONDITION: Namespace $existing_ns 已存在且不是本腳本建立（缺 label $OWNER_LABEL），拒絕執行以免刪除既有資源；請改名或自行處理。" >&2
    rm -rf "$lock_dir"
    exit 2
  fi
done
# 清理只依「本次執行 create 成功」的旗標，不依事後查詢 label：create 遇到既有 Namespace 會失敗（不會接管他人資源），
# 標記失敗也不會讓已建立的 Namespace 殘留。
ns_created=0
np_ns_created=0
ns_uid=""
np_ns_uid=""
namespace_uid() { k get namespace "$1" -o jsonpath='{.metadata.uid}' 2>/dev/null; }
# UID 直接取自 create 的回應（與建立同一次 API 呼叫），拿不到就視為建立失敗、不記為「本次建立」；
# 刪除前核對 UID，Namespace 若在執行期間被換成同名的別人資源，UID 不符就不刪。
delete_created_namespace() { # delete_created_namespace <name> <uid>
  [ -n "$2" ] && [ "$(namespace_uid "$1")" = "$2" ] && k delete namespace "$1" --ignore-not-found --wait=true --timeout=180s >/dev/null 2>&1
}
cleanup() {
  stop_background
  if [ "${KEEP_UP:-0}" != "1" ] && [ "$ns_created" = "1" ]; then delete_created_namespace "$NS" "$ns_uid"; fi
  if [ "$np_ns_created" = "1" ]; then delete_created_namespace "$NP_NS" "$np_ns_uid"; fi
  [ "$holds_lock" = "1" ] && [ "$(cat "$lock_dir/pid" 2>/dev/null)" = "$$" ] && rm -rf "$lock_dir"
  rm -rf "$work_dir"
}
trap cleanup EXIT

# 套用前把 manifest 的 Namespace 與 NodePort 換成驗證專用值，不改檔案本身。
render() {
  sed -e 's/^\( *namespace:\) projectc$/\1 projectc-verify/' \
      -e 's/^\( *name:\) projectc$/\1 projectc-verify/' \
      -e 's/nodePort: 30080/nodePort: 30081/' "$K8S_DIR/$1.yaml"
}
apply_manifest() { render "$1" | k apply -f - 2>&1; }

# 殘留的驗證 Namespace 先清掉，確保從乾淨狀態開始。
# 守衛已確認這些 Namespace 若存在必為本腳本建立。
for stale_ns in "$NS" "$NP_NS"; do
  owns_namespace "$stale_ns" && k delete namespace "$stale_ns" --ignore-not-found --wait=true --timeout=180s >/dev/null 2>&1
done

# ---- 前置斷言：覆寫結果正確（斷言 3、9 與突變 e 都依賴此變數） ----
ns_render="$(render 00-namespace)"
web_render="$(render 50-web)"
override_problems=""
grep -q '^ *name: projectc-verify$' <<<"$ns_render" || override_problems+="Namespace manifest 未被改為 projectc-verify; "
for name in "${MANIFESTS[@]}"; do
  if render "$name" | grep -qE '^ *(namespace|name): projectc$'; then override_problems+="$name 仍含 projectc Namespace; "; fi
done
grep -q "nodePort: ${VERIFY_NODEPORT}" <<<"$web_render" || override_problems+="web NodePort 未被覆寫為 ${VERIFY_NODEPORT}; "
if [ -z "$override_problems" ]; then pass 0 "manifest 覆寫為 Namespace $NS、NodePort $VERIFY_NODEPORT"; else fail 0 "覆寫結果不符：$override_problems"; finish; fi

# ---- 斷言 1：依序套用並等待就緒 ----
if ns_uid="$(render 00-namespace | k create -f - -o jsonpath='{.metadata.uid}' 2>&1)" && [ -n "$ns_uid" ]; then
  ns_created=1
  apply_out="namespace/$NS created"
else
  fail 1 "建立 Namespace $NS 失敗（已存在、權限不足或回應無 UID；不接管既有 Namespace）" "$ns_uid"
  ns_uid=""
  finish
fi
if ! label_out="$(k label namespace "$NS" "$OWNER_LABEL" 2>&1)"; then
  fail 1 "標記 Namespace $NS 失敗" "$label_out"
  finish
fi
apply_out+=$'\n'"$(apply_manifest 10-configmap)"
pg_user="$(kn get configmap projectc-config -o jsonpath='{.data.POSTGRES_USER}' 2>/dev/null)"
pg_db="$(kn get configmap projectc-config -o jsonpath='{.data.POSTGRES_DB}' 2>/dev/null)"
if [ -z "$pg_user" ] || [ -z "$pg_db" ]; then
  fail 1 "ConfigMap projectc-config 缺 POSTGRES_USER／POSTGRES_DB" "$apply_out"
  finish
fi

pg_password="$(random_hex 16)"
jwt_key="$(random_hex 32)"
ticket_key="$(random_hex 32)"
admin_password="Aa1-$(random_hex 12)"
conn_string="Host=db;Port=5432;Database=${pg_db};Username=${pg_user};Password=${pg_password}"
umask 077
{
  echo "POSTGRES_PASSWORD=${pg_password}"
  echo "ConnectionStrings__DefaultConnection=${conn_string}"
  echo "Jwt__SigningKey=${jwt_key}"
  echo "TicketSigning__SigningKey=${ticket_key}"
  echo "DevelopmentSeed__AdminPassword=${admin_password}"
} > "$work_dir/secret.env"
grep -v '^Jwt__SigningKey=' "$work_dir/secret.env" > "$work_dir/secret-missing.env"
kn create secret generic projectc-secrets --from-env-file="$work_native/secret.env" >/dev/null 2>&1 \
  || { fail 1 "建立 Secret 失敗"; finish; }
kn create secret generic projectc-secrets-missing --from-env-file="$work_native/secret-missing.env" >/dev/null 2>&1 \
  || { fail 1 "建立缺鍵 Secret 失敗"; finish; }
declare -A secret_values=(
  [POSTGRES_PASSWORD]="$pg_password"
  [ConnectionStrings__DefaultConnection]="$conn_string"
  [Jwt__SigningKey]="$jwt_key"
  [TicketSigning__SigningKey]="$ticket_key"
  [DevelopmentSeed__AdminPassword]="$admin_password"
)

wait_rollout() { kn rollout status "$1" --timeout="${2:-240s}" 2>&1; }

step1_problems=""
apply_out="$(apply_manifest 20-postgres)"$'\n'"$(apply_manifest 21-redis)"$'\n'"$(apply_manifest 22-seq)"
for target in deployment/db deployment/redis deployment/seq; do
  out="$(wait_rollout "$target")" || step1_problems+="$target 未就緒：$out"$'\n'
done
apply_out+=$'\n'"$(apply_manifest 30-migrator-job)"
job_out="$(kn wait --for=condition=complete job/migrator --timeout=300s 2>&1)" || step1_problems+="migrator Job 未 Complete：$job_out"$'\n'
apply_out+=$'\n'"$(apply_manifest 40-api)"
out="$(wait_rollout deployment/api 360s)" || step1_problems+="deployment/api 未就緒：$out"$'\n'
apply_out+=$'\n'"$(apply_manifest 50-web)"
out="$(wait_rollout deployment/web)" || step1_problems+="deployment/web 未就緒：$out"$'\n'
apply_out+=$'\n'"$(apply_manifest 60-networkpolicy)"

expected_migrations="$(ls "$MIGRATIONS_DIR"/*.Designer.cs | wc -l | tr -d ' ')"
applied="$(kn exec deploy/db -- psql -U "$pg_user" -d "$pg_db" -tA -c 'select count(*) from "__EFMigrationsHistory"' 2>&1 | tr -d '\r ')"
[ "$applied" = "$expected_migrations" ] || step1_problems+="__EFMigrationsHistory 筆數=$applied，預期 $expected_migrations"$'\n'
max_restarts="$(kn get pods -l app=api -o jsonpath='{range .items[*]}{.status.containerStatuses[0].restartCount}{"\n"}{end}' 2>/dev/null | sort -n | tail -n 1)"
[ "${max_restarts:-99}" -le 1 ] || step1_problems+="api 重啟次數 $max_restarts 超過 1"$'\n'
if [ -z "$step1_problems" ]; then
  pass 1 "db／redis／seq／api／web 就緒（套用 NetworkPolicy 後仍就緒），migrator Complete，migration 筆數 $applied（預期 $expected_migrations），api 最大 restartCount=${max_restarts}"
else
  fail 1 "就緒或 migration 檢查失敗" "$step1_problems"$'\n'"$(kn get pods 2>&1)"$'\n'"$apply_out"
  finish
fi

# ---- 斷言 2：探針設定與 DB 不可用時 readiness ----
probe_json="$(kn get deployment api -o json)"
live_path="$(jq -r '.spec.template.spec.containers[0].livenessProbe.httpGet.path // "none"' <<<"$probe_json")"
ready_path="$(jq -r '.spec.template.spec.containers[0].readinessProbe.httpGet.path // "none"' <<<"$probe_json")"
startup_path="$(jq -r '.spec.template.spec.containers[0].startupProbe.httpGet.path // "none"' <<<"$probe_json")"
if [ "$live_path" = "/health/live" ] && [ "$ready_path" = "/health/ready" ] && [ "$startup_path" = "/health/live" ]; then
  pass 2a "探針路徑：liveness=$live_path readiness=$ready_path startup=$startup_path"
else
  fail 2a "探針路徑不符：liveness=$live_path readiness=$ready_path startup=$startup_path（預期 /health/live、/health/ready、/health/live）"
fi

api_endpoints() { kn get endpoints api -o jsonpath='{.subsets[*].addresses[*].ip}' 2>/dev/null; }
poll_until() { # poll_until <timeout 秒> <指令...>：每 3 秒執行一次，成功即回傳 0
  local timeout="$1"; shift
  local waited=0
  while [ "$waited" -lt "$timeout" ]; do
    if "$@"; then return 0; fi
    sleep 3; waited=$((waited + 3))
  done
  return 1
}
endpoints_empty() { [ -z "$(api_endpoints)" ]; }
endpoints_present() { [ -n "$(api_endpoints)" ]; }

kn scale deployment/db --replicas=0 >/dev/null 2>&1
if poll_until 90 endpoints_empty; then
  pass 2b "db 停止後 api Service endpoints 在 90 秒內清空（readiness 轉為 NotReady）"
else
  fail 2b "db 停止後 90 秒內 api endpoints 仍非空" "$(api_endpoints)"
fi
kn scale deployment/db --replicas=1 >/dev/null 2>&1
if wait_rollout deployment/db >/dev/null && poll_until 150 endpoints_present; then
  pass 2c "db 恢復後 api endpoints 在 150 秒內回到 Ready"
else
  fail 2c "db 恢復後 api 未回到 Ready" "$(kn get pods 2>&1)"
  finish
fi

# ---- 斷言 3：經 NodePort 的 web 與 /api ----
http_code() { curl -s -o /dev/null -m 15 -w '%{http_code}' "$@"; }
root_code="$(http_code "$BASE/")"
events_code="$(http_code "$BASE/api/events?page=1&pageSize=1")"
if [ "$root_code" = "200" ]; then pass 3a "GET $BASE/ → 200"; else fail 3a "GET $BASE/ → $root_code（預期 200）"; fi
if [[ "$events_code" =~ ^2[0-9][0-9]$ ]]; then pass 3b "GET /api/events → $events_code（2xx，非 3xx）"; else fail 3b "GET /api/events → $events_code（預期 2xx）"; fi

# ---- 斷言 4、5：持續請求下的 Pod 刪除與滾動更新 ----
start_load() { # start_load <輸出檔>：背景每 0.2 秒請求 /api/events，直到 <輸出檔>.stop 出現
  local out="$1"
  : > "$out"
  (
    while [ ! -e "$out.stop" ]; do
      curl -s -o /dev/null -m 10 -w '%{http_code}\n' "$BASE/api/events?page=1&pageSize=1" >> "$out"
      sleep 0.2
    done
  ) &
  bg_pids+=("$!")
  load_pid="$!"
}
stop_load() { # stop_load <輸出檔>
  touch "$1.stop"
  wait "$load_pid" 2>/dev/null
}
summarize_load() { # summarize_load <輸出檔> → 「總數 非2xx數」
  local total bad
  total="$(wc -l < "$1" | tr -d ' ')"
  bad="$(grep -vc '^2[0-9][0-9]$' "$1" || true)"
  echo "$total ${bad:-0}"
}
readonly MIN_LOAD_REQUESTS=20

victim="$(kn get pods -l app=api -o jsonpath='{.items[0].metadata.name}')"
start_load "$work_dir/load4"
kn delete pod "$victim" --wait=true >/dev/null 2>&1
recovered=1
api_fully_ready() { [ "$(kn get deployment api -o jsonpath='{.status.readyReplicas}' 2>/dev/null)" = "2" ]; }
poll_until 180 api_fully_ready || recovered=0
stop_load "$work_dir/load4"
read -r total bad <<<"$(summarize_load "$work_dir/load4")"
if [ "$recovered" -eq 1 ] && [ "$total" -ge "$MIN_LOAD_REQUESTS" ] && [ "$bad" -eq 0 ]; then
  pass 4 "刪除 Pod $victim 期間 $total 次請求全為 2xx，Deployment 回到 2 個 Ready"
else
  fail 4 "recovered=$recovered，請求總數=$total（至少 $MIN_LOAD_REQUESTS），非 2xx=$bad" "$(sort "$work_dir/load4" | uniq -c)"$'\n'"$(kn get pods 2>&1)"
fi

start_load "$work_dir/load5"
kn rollout restart deployment/api >/dev/null 2>&1
rollout_out="$(wait_rollout deployment/api 360s)"; rollout_exit=$?
stop_load "$work_dir/load5"
read -r total bad <<<"$(summarize_load "$work_dir/load5")"
if [ "$rollout_exit" -eq 0 ] && [ "$total" -ge "$MIN_LOAD_REQUESTS" ] && [ "$bad" -eq 0 ]; then
  pass 5 "rollout restart 期間 $total 次請求全為 2xx"
else
  fail 5 "rollout 結束碼=$rollout_exit，請求總數=$total（至少 $MIN_LOAD_REQUESTS），非 2xx=$bad" "$(sort "$work_dir/load5" | uniq -c)"$'\n'"$rollout_out"
fi

# ---- 斷言 6：機敏值不外洩 ----
{
  for pod in $(kn get pods -o name 2>/dev/null); do kn logs "$pod" --all-containers --tail=-1 2>&1; done
  kn logs job/migrator --tail=-1 2>&1
  kn describe pods,deployments,jobs,services,configmaps 2>&1
} > "$work_dir/observed.txt"
leaks=""
for key in "${SECRET_KEYS[@]}"; do
  value="${secret_values[$key]}"
  grep -Fq -e "$value" "$work_dir/observed.txt" && leaks+="$key 出現在 logs／describe; "
  grep -Frq -e "$value" "$K8S_DIR" && leaks+="$key 出現在 $K8S_DIR/; "
  git grep -Fq -e "$value" 2>/dev/null && leaks+="$key 出現在版控檔案; "
done
cm_keys="$(kn get configmap projectc-config -o json | jq -r '.data | keys[]')"
for key in "${SECRET_KEYS[@]}"; do
  grep -Fq -- "$key" "$K8S_DIR/10-configmap.yaml" && leaks+="Secret 鍵名 $key 出現在 $K8S_DIR/10-configmap.yaml; "
  grep -Fxq -- "$key" <<<"$cm_keys" && leaks+="Secret 鍵名 $key 出現在叢集的 ConfigMap; "
done
if [ -z "$leaks" ]; then pass 6 "5 個隨機機敏值未出現在 logs、describe、k8s/、版控；Secret 鍵名不在 ConfigMap"; else fail 6 "$leaks"; fi

# ---- 斷言 7：缺設定演練（暫存 Deployment 複製 api 規格，Secret 來源改為缺 Jwt__SigningKey 的副本） ----
# 標籤用 app=api-missing-config，不帶 app=api，避免被 Service api 選到而干擾斷言 4、5。
# 此 Pod 不在 NetworkPolicy 的 podSelector 內（db 只收 app=api），但它在設定驗證階段就退出，不會連 db。
kn get deployment api -o json | jq '{apiVersion, kind, metadata: {name: "api-missing-config", namespace: .metadata.namespace},
  spec: (.spec | {replicas: 1, selector: {matchLabels: {app: "api-missing-config"}},
    template: (.template | .metadata = {labels: {app: "api-missing-config"}} | .spec.containers[0].envFrom |= map(if .secretRef then .secretRef.name = "projectc-secrets-missing" else . end))})}' \
  | k apply -f - >/dev/null 2>&1
reason=""
waited=0
while [ "$waited" -lt 180 ]; do
  reason="$(kn get pods -l app=api-missing-config -o jsonpath='{.items[0].status.containerStatuses[0].state.waiting.reason}' 2>/dev/null)"
  [ "$reason" = "CrashLoopBackOff" ] && break
  sleep 3; waited=$((waited + 3))
done
exit_code="$(kn get pods -l app=api-missing-config -o jsonpath='{.items[0].status.containerStatuses[0].lastState.terminated.exitCode}' 2>/dev/null)"
missing_logs="$(kn logs -l app=api-missing-config --tail=20 2>&1)"
if [ "$reason" = "CrashLoopBackOff" ] && [ -n "$exit_code" ] && [ "$exit_code" != "0" ]; then
  pass 7 "缺 Jwt__SigningKey 時 api 進入 CrashLoopBackOff，上次結束碼=$exit_code"
  echo "---- 斷言 7 日誌（最後 20 行）----"; echo "$missing_logs"; echo "----------------------------------"
else
  fail 7 "狀態=$reason，上次結束碼=$exit_code" "$missing_logs"
fi
kn delete deployment api-missing-config --ignore-not-found >/dev/null 2>&1

# ---- 斷言 8：/health/* 不可經 web 取得 ----
body_of() { curl -s -m 15 "$BASE$1"; }
health_problems=""
for path in /health/live /health/ready /api/health/live /api/health/ready; do
  body="$(body_of "$path")"
  if [ "$(echo "$body" | tr -d '[:space:]')" = "Healthy" ] || grep -qE '"status" *: *"Healthy"' <<<"$body"; then
    health_problems+="$path 回應為 api 健康檢查結果; "
  fi
done
live_body="$(body_of /health/live)"
grep -qi '<html' <<<"$live_body" || health_problems+="/health/live 回應不是 SPA index.html（預期 nginx try_files 回 index.html）; "
if [ -z "$health_problems" ]; then pass 8a "經 web 取不到 /health/live、/health/ready、/api/health/*（回應不是 Healthy）"; else fail 8a "$health_problems"; fi

pf_log="$work_dir/pf.log"
kn port-forward service/api :8080 > "$pf_log" 2>&1 &
bg_pids+=("$!")
pf_port=""
for _ in $(seq 1 20); do
  pf_port="$(grep -oE '127\.0\.0\.1:[0-9]+' "$pf_log" | head -n 1 | cut -d: -f2)"
  [ -n "$pf_port" ] && break
  sleep 1
done
pf_body=""
[ -n "$pf_port" ] && pf_body="$(curl -s -m 15 "http://127.0.0.1:${pf_port}/health/live")"
if [ "$pf_body" = "Healthy" ]; then pass 8b "kubectl port-forward 到 api 可取得 /health/live（維運來源可達）"; else fail 8b "port-forward 取得 /health/live 失敗：'$pf_body'" "$(cat "$pf_log")"; fi

# 不可信 workload 不可達：先做對照實驗，判斷本機 CNI 是否執行 NetworkPolicy。
tcp_reachable() { # tcp_reachable <namespace> <pod> <host> <port>
  k -n "$1" exec "$2" -- nc -z -w 3 "$3" "$4" >/dev/null 2>&1
}
if np_ns_uid="$(k create namespace "$NP_NS" -o jsonpath='{.metadata.uid}' 2>&1)" && [ -n "$np_ns_uid" ]; then np_ns_created=1; else np_create_out="$np_ns_uid"; np_ns_uid=""; fi
if [ "$np_ns_created" != "1" ] || ! np_create_out="$(k label namespace "$NP_NS" "$OWNER_LABEL" 2>&1)"; then
  fail 8c "建立或標記 NetworkPolicy 測試 Namespace $NP_NS 失敗" "$np_create_out"
  finish
fi
k -n "$NP_NS" run srv --image=redis:7-alpine --image-pull-policy=IfNotPresent --labels=app=srv >/dev/null 2>&1
k -n "$NP_NS" run cli --image=redis:7-alpine --image-pull-policy=IfNotPresent --command -- sleep 600 >/dev/null 2>&1
k -n "$NP_NS" wait --for=condition=Ready pod/srv pod/cli --timeout=120s >/dev/null 2>&1
srv_ip="$(k -n "$NP_NS" get pod srv -o jsonpath='{.status.podIP}' 2>/dev/null)"
if [ -z "$srv_ip" ] || ! tcp_reachable "$NP_NS" cli "$srv_ip" 6379; then
  fail 8c "對照實驗前置失敗：未套用 NetworkPolicy 時 cli 就連不到 srv（$srv_ip:6379）"
else
  cat <<EOF | k -n "$NP_NS" apply -f - >/dev/null 2>&1
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata: {name: deny-all}
spec:
  podSelector: {matchLabels: {app: srv}}
  policyTypes: [Ingress]
EOF
  sleep 5
  if tcp_reachable "$NP_NS" cli "$srv_ip" 6379; then
    mark_unverified 8c "本機 CNI 不執行 NetworkPolicy（deny-all 保護的 Pod 仍可連通），無法驗證不可信 workload 不可達；見 docs/todo/2026-11-10-networkpolicy-enforcement-todo.md"
  else
    untrusted_problems=""
    kn run untrusted --image=redis:7-alpine --image-pull-policy=IfNotPresent --labels=app=untrusted --command -- sleep 600 >/dev/null 2>&1
    kn run webclient --image=redis:7-alpine --image-pull-policy=IfNotPresent --labels=app=web --command -- sleep 600 >/dev/null 2>&1
    kn wait --for=condition=Ready pod/untrusted pod/webclient --timeout=120s >/dev/null 2>&1
    tcp_reachable "$NS" untrusted api 8080 && untrusted_problems+="不可信 Pod 連得到 api:8080; "
    tcp_reachable "$NS" untrusted redis 6379 && untrusted_problems+="不可信 Pod 連得到 redis:6379; "
    tcp_reachable "$NS" untrusted db 5432 && untrusted_problems+="不可信 Pod 連得到 db:5432; "
    tcp_reachable "$NS" webclient api 8080 || untrusted_problems+="app=web 的 Pod 連不到 api:8080; "
    if [ -z "$untrusted_problems" ]; then pass 8c "CNI 有執行 NetworkPolicy：不可信 Pod 連不到 api／redis／db，app=web 可連 api"; else fail 8c "$untrusted_problems"; fi
  fi
fi

# ---- 斷言 9：暴露面 ----
svc_types="$(kn get services -o json | jq -r '.items[] | "\(.metadata.name) \(.spec.type)"')"
expose_problems=""
[ "$(grep '^api ' <<<"$svc_types")" = "api ClusterIP" ] || expose_problems+="api Service 不是 ClusterIP; "
[ "$(grep -c -E ' (NodePort|LoadBalancer)$' <<<"$svc_types" || true)" -eq 1 ] || expose_problems+="NodePort／LoadBalancer Service 數量不是 1; "
[ "$(grep '^web ' <<<"$svc_types")" = "web NodePort" ] || expose_problems+="web Service 不是 NodePort; "
web_nodeport="$(kn get service web -o jsonpath='{.spec.ports[0].nodePort}')"
[ "$web_nodeport" = "$VERIFY_NODEPORT" ] || expose_problems+="web nodePort=$web_nodeport; "
for resource in deployment/api deployment/web job/migrator; do
  non_root="$(kn get "$resource" -o json | jq -r '[.spec.template.spec.containers[].securityContext.runAsNonRoot // false] | all')"
  [ "$non_root" = "true" ] || expose_problems+="$resource 的容器未設 runAsNonRoot: true; "
done
for path in /swagger /openapi/v1.json /api/openapi/v1.json; do
  body="$(body_of "$path")"
  if grep -qiE '"openapi" *:|swagger-ui|Swagger UI' <<<"$body"; then expose_problems+="$path 經 web 取得 OpenAPI／SwaggerUI; "; fi
done
if [ -z "$expose_problems" ]; then
  pass 9 "api 為 ClusterIP、僅 web 為 NodePort（$web_nodeport）、api／web／migrator 皆 runAsNonRoot、OpenAPI／SwaggerUI 不可經 web 取得"
else
  fail 9 "$expose_problems" "$svc_types"
fi

finish
