#!/usr/bin/env bash
# production 映像的替代驗證（production-images design.md 決策 5）：建置 api／migrator／web 映像，
# 以獨立 compose 專案 projectc-verify 起 db／redis／api／web，逐項斷言；任一失敗即非 0 結束。
# 用法：bash scripts/verify-production-images.sh
#   KEEP_UP=1  驗證後保留 projectc-verify 的容器（供真實瀏覽器檢查），並印出 web 對外位址；用完請自行
#              執行 `docker compose -f deploy/docker-compose.verify.yml --profile migrate down -v --remove-orphans`。
# 機敏值於執行時隨機產生並以環境變數傳入，不寫入檔案；本腳本不使用 set -x 或 `docker compose config`
# （兩者都會展開機敏值）。只透過 docker 操作，不在本機執行 dotnet／psql。
set -uo pipefail
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

cd "$(dirname "$0")/.."

readonly COMPOSE_FILE="deploy/docker-compose.verify.yml"
readonly IMAGE_API="projectc-verify/api:test"
readonly IMAGE_MIGRATOR="projectc-verify/migrator:test"
readonly IMAGE_WEB="projectc-verify/web:test"
readonly DEV_SETTINGS="src/ProjectC.WebApi/appsettings.Development.json"
readonly MIGRATIONS_DIR="src/ProjectC.Infrastructure/Persistence/Migrations"
readonly MAX_BODY_BYTES=10485760

compose() { docker compose -f "$COMPOSE_FILE" "$@"; }

random_hex() { od -An -N"$1" -tx1 /dev/urandom | tr -d ' \n'; }

export VERIFY_POSTGRES_USER="verify"
export VERIFY_POSTGRES_DB="verify"
VERIFY_POSTGRES_PASSWORD="$(random_hex 16)"
VERIFY_JWT_SIGNING_KEY="$(random_hex 32)"
VERIFY_TICKET_SIGNING_KEY="$(random_hex 32)"
export VERIFY_POSTGRES_PASSWORD VERIFY_JWT_SIGNING_KEY VERIFY_TICKET_SIGNING_KEY

work_dir="$(mktemp -d)"
# Windows 原生 curl 讀不到 MSYS 的 /tmp 路徑，需轉成原生路徑；沒有 cygpath 的環境（Linux／macOS）原樣使用。
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }
work_native="$(native_path "$work_dir")"
created_dev_settings=0
passed=0
failed=0

cleanup() {
  if [ "$created_dev_settings" -eq 1 ]; then rm -f "$DEV_SETTINGS"; fi
  if [ "${KEEP_UP:-0}" != "1" ]; then
    compose --profile migrate down -v --remove-orphans >/dev/null 2>&1
  fi
  rm -rf "$work_dir"
}
trap cleanup EXIT

pass() { echo "PASS [$1] $2"; passed=$((passed + 1)); }
fail() {
  echo "FAIL [$1] $2"
  if [ -n "${3:-}" ]; then echo "---- 實際輸出 ----"; echo "$3" | tail -n 40; echo "------------------"; fi
  failed=$((failed + 1))
}
finish() {
  echo "SUMMARY: ${passed} passed, ${failed} failed"
  [ "$failed" -eq 0 ]
  exit $?
}

http_code() { curl -s -o /dev/null -m 30 -w '%{http_code}' "$@"; }

# 斷言 8 的前置：appsettings.Development.json 被 .gitignore 排除，乾淨 checkout 不存在，
# 直接檢查會 vacuously 通過。放一個 dummy 同名檔，確保 .dockerignore 規則真的被測到。
if [ ! -e "$DEV_SETTINGS" ]; then
  echo '{ "Dummy": "verify-production-images" }' > "$DEV_SETTINGS"
  created_dev_settings=1
fi
if [ ! -f "$DEV_SETTINGS" ]; then
  fail 8-precondition "build context 內沒有 $DEV_SETTINGS，.dockerignore 排除無法被驗證"
  finish
fi

# ---- 斷言 1：映像建置 ----
build_ok=1
for spec in "runtime|$IMAGE_API|deploy/api.Dockerfile|." "migrator|$IMAGE_MIGRATOR|deploy/api.Dockerfile|." "web|$IMAGE_WEB|deploy/web.Dockerfile|web"; do
  IFS="|" read -r target image dockerfile context <<<"$spec"
  target_args=(--target "$target")
  context_args=()
  [ "$target" = "web" ] && { target_args=(); context_args=(--build-context deploy=deploy); }
  output="$(docker build -f "$dockerfile" "${target_args[@]}" "${context_args[@]}" -t "$image" "$context" 2>&1)"
  if [ $? -eq 0 ]; then pass 1 "建置 $image"; else fail 1 "建置 $image 失敗" "$output"; build_ok=0; fi
done
if [ "$build_ok" -ne 1 ]; then echo "映像建置失敗，後續斷言無法執行"; finish; fi

# ---- 斷言 2：非 root、不含 SDK ----
uid="$(docker run --rm --entrypoint id "$IMAGE_API" -u 2>&1)"
if [ "$uid" != "0" ] && [[ "$uid" =~ ^[0-9]+$ ]]; then pass 2a "runtime 使用者 UID=$uid（非 root）"; else fail 2a "runtime 使用者不是非 root 數字 UID" "$uid"; fi
sdks="$(docker run --rm --entrypoint dotnet "$IMAGE_API" --list-sdks 2>&1)"
if [ -z "$sdks" ]; then pass 2b "runtime 映像不含 SDK"; else fail 2b "runtime 映像含 SDK" "$sdks"; fi

# ---- 斷言 8：runtime 映像不含 appsettings.Development.json ----
found="$(docker run --rm --entrypoint sh "$IMAGE_API" -c 'find / -name appsettings.Development.json -not -path "/proc/*" 2>/dev/null')"
if [ -z "$found" ]; then pass 8 "runtime 映像內不存在 appsettings.Development.json"; else fail 8 "runtime 映像內存在 appsettings.Development.json" "$found"; fi

# ---- 斷言 3：migrator 對空資料庫套用全部 migration ----
expected_migrations="$(ls "$MIGRATIONS_DIR"/*.Designer.cs | wc -l | tr -d ' ')"
compose up -d --wait db >/dev/null 2>&1
migrate_output="$(compose --profile migrate run --rm migrator 2>&1)"
migrate_exit=$?
applied="$(compose exec -T db psql -U "$VERIFY_POSTGRES_USER" -d "$VERIFY_POSTGRES_DB" -tA -c 'select count(*) from "__EFMigrationsHistory"' 2>&1 | tr -d '\r ')"
if [ "$migrate_exit" -eq 0 ] && [ "$applied" = "$expected_migrations" ]; then
  pass 3 "migrator 套用 $applied 筆 migration（預期 $expected_migrations）"
else
  fail 3 "migrator 結束碼=$migrate_exit，__EFMigrationsHistory 筆數=$applied，預期 $expected_migrations" "$migrate_output"
fi

# ---- 斷言 7（負向）：僅缺 Jwt__SigningKey 時 api 啟動失敗 ----
neg_output="$(compose run --rm --no-deps -e Jwt__SigningKey= api 2>&1)"
neg_exit=$?
if [ "$neg_exit" -ne 0 ] && grep -q 'OptionsValidationException' <<<"$neg_output" && grep -q 'JwtOptions' <<<"$neg_output"; then
  pass 7 "缺 Jwt__SigningKey 時 api 以結束碼 $neg_exit 退出，日誌含 OptionsValidationException 與 JwtOptions"
else
  fail 7 "缺 Jwt__SigningKey 的負向行為不符（結束碼=$neg_exit）" "$neg_output"
fi

# ---- 起 api／web ----
compose up -d api web >/dev/null 2>&1
api_address="$(compose port api 8080 2>/dev/null | tr -d '\r')"
web_address="$(compose port web 8080 2>/dev/null | tr -d '\r')"
api_url="http://${api_address/0.0.0.0/127.0.0.1}"
web_url="http://${web_address/0.0.0.0/127.0.0.1}"

# ---- 斷言 4：health ----
ready_code=000
for _ in $(seq 1 60); do
  ready_code="$(http_code "$api_url/health/ready")"
  [ "$ready_code" = "200" ] && break
  sleep 1
done
live_code="$(http_code "$api_url/health/live")"
if [ "$live_code" = "200" ] && [ "$ready_code" = "200" ]; then
  pass 4 "/health/live=$live_code，/health/ready=$ready_code"
else
  fail 4 "/health/live=$live_code，/health/ready=$ready_code" "$(compose logs api 2>&1)"
fi

# ---- 斷言 5：經 nginx 的 /api 可達、回 2xx（證實 HTTP-only 下無 HTTPS 轉址）----
events_code="$(http_code "$web_url/api/events")"
if [[ "$events_code" =~ ^2[0-9][0-9]$ ]]; then
  pass 5 "經 web 的 GET /api/events 回 $events_code"
else
  fail 5 "經 web 的 GET /api/events 回 $events_code（預期 2xx）" "$(curl -s -i -m 30 "$web_url/api/events" | head -n 15)"
fi

# ---- 斷言 6：SPA 回退與快取標頭 ----
route_headers="$(curl -s -D - -o "$work_native/route.html" -m 30 "$web_url/events/1")"
route_code="$(head -n 1 <<<"$route_headers" | awk '{print $2}')"
if [ "$route_code" = "200" ] && grep -qi '<div id="app"' "$work_dir/route.html" && grep -qi '^cache-control:.*no-cache' <<<"$route_headers"; then
  pass 6a "SPA 子路由 /events/1 回 200、為 index.html 且 Cache-Control 含 no-cache"
else
  fail 6a "SPA 子路由 /events/1 不符（狀態=$route_code）" "$route_headers"
fi
asset_path="$(grep -oE '/assets/[A-Za-z0-9._-]+\.js' "$work_dir/route.html" | head -n 1)"
asset_headers=""
[ -n "$asset_path" ] && asset_headers="$(curl -s -D - -o /dev/null -m 30 "$web_url$asset_path")"
if [ -n "$asset_path" ] && head -n 1 <<<"$asset_headers" | grep -q ' 200' && grep -qi '^cache-control:.*immutable' <<<"$asset_headers"; then
  pass 6b "$asset_path 回 200 且 Cache-Control 含 immutable"
else
  fail 6b "靜態資產 '$asset_path' 不符" "$asset_headers"
fi

# ---- 斷言 9：nginx 本文上限不低於 API 最大單端點上限（送達 API 才會得到 ProblemDetails 413）----
# /api/auth/login 為匿名且確實讀取 body 的 POST 端點；以 Content-Length 傳送（curl --data-binary）。
head -c "$MAX_BODY_BYTES" /dev/zero | tr '\0' 'a' > "$work_dir/body.bin"
body_response="$(curl -s -i -m 60 -X POST -H 'Content-Type: application/json' --data-binary "@$work_native/body.bin" "$web_url/api/auth/login")"
if grep -q '"title":"PayloadTooLarge"' <<<"$body_response"; then
  pass 9 "經 nginx 送 $MAX_BODY_BYTES bytes 至 /api/auth/login，回應來自 API（ProblemDetails PayloadTooLarge）"
else
  fail 9 "經 nginx 送 $MAX_BODY_BYTES bytes 至 /api/auth/login 的回應不是 API 的 PayloadTooLarge ProblemDetails" "$(head -c 1500 <<<"$body_response")"
fi

if [ "${KEEP_UP:-0}" = "1" ]; then echo "KEEP_UP：web=$web_url api=$api_url"; fi
finish
