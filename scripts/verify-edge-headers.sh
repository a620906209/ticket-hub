#!/usr/bin/env bash
# 邊界 nginx 的安全標頭／HSTS／gzip 驗證（edge-security-headers spec ESH-001～005、007、008、010～012）。
# 以 projectc/web:local 起一個 web 容器，把工作樹的 deploy/nginx 設定複製進去；另起同映像的容器掛網路別名
# `api` 當上游替身（回 200／404／500 的固定小回應），逐路徑斷言；任一 FAIL 即非 0 結束。
# 用法：bash scripts/verify-edge-headers.sh
# 前置：已建好 projectc/web:local（指令見 deploy/web.Dockerfile 開頭註解）；本機需有 docker、curl、gzip。
# 結束碼：0 全 PASS；1 有 FAIL；2 前置條件不符。暫時容器與 network 於結束時清除。
set -uo pipefail
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'

cd "$(dirname "$0")/.."

readonly IMAGE="projectc/web:local"
readonly RUN_ID="edge-verify-$$"
readonly NETWORK="$RUN_ID-net"
readonly WEB="$RUN_ID-web"
readonly STUB="$RUN_ID-api"
readonly NGINX_DIR="deploy/nginx"
readonly SNIPPET_SOURCE="$NGINX_DIR/security-headers.conf"
readonly SNIPPET_TARGET="etc/nginx/snippets/security-headers.conf"

readonly EXPECTED_CSP="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'"
readonly EXPECTED_HEADERS=(
  "Content-Security-Policy|$EXPECTED_CSP"
  "X-Content-Type-Options|nosniff"
  "X-Frame-Options|DENY"
  "Referrer-Policy|strict-origin-when-cross-origin"
  "Permissions-Policy|camera=(self), microphone=(), geolocation=()"
)

passed=0
failed=0
pass() { echo "PASS [$1] $2"; passed=$((passed + 1)); }
fail() { echo "FAIL [$1] $2"; failed=$((failed + 1)); }

work_dir="$(mktemp -d)"
# Windows 原生 curl 讀寫不到 MSYS 的 /tmp 路徑，需轉成原生路徑；沒有 cygpath 的環境原樣使用。
native_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }
work_native="$(native_path "$work_dir")"
cleanup() {
  docker rm -f "$WEB" "$STUB" >/dev/null 2>&1
  docker network rm "$NETWORK" >/dev/null 2>&1
  rm -rf "$work_dir"
}
trap cleanup EXIT

command -v curl >/dev/null 2>&1 && command -v gzip >/dev/null 2>&1 || { echo "缺少 curl 或 gzip"; exit 2; }
docker image inspect "$IMAGE" >/dev/null 2>&1 || { echo "找不到映像 $IMAGE，請先依 deploy/web.Dockerfile 開頭註解建置"; exit 2; }

# docker cp 不會建立不存在的目標目錄，改用 tar 串流整棵目錄樹複製進容器。
copy_tree_into() {
  local container="$1" stage="$2"
  tar -C "$stage" -cf - . | docker cp - "$container:/"
}

stage_stub="$work_dir/stub"
mkdir -p "$stage_stub/etc/nginx/conf.d"
cat > "$stage_stub/etc/nginx/conf.d/default.conf" <<'EOF'
server {
    listen 8080;
    default_type application/json;
    location = /api/ok { return 200 '{"ok":true}'; }
    location = /api/client-error { return 404 '{"error":"client"}'; }
    location = /api/server-error { return 500 '{"error":"server"}'; }
}
EOF

stage_web="$work_dir/web"
mkdir -p "$stage_web/etc/nginx/conf.d" "$stage_web/etc/nginx/snippets"
cp "$NGINX_DIR/default.conf" "$stage_web/etc/nginx/conf.d/default.conf"
[ -f "$SNIPPET_SOURCE" ] && cp "$SNIPPET_SOURCE" "$stage_web/$SNIPPET_TARGET"

docker network create "$NETWORK" >/dev/null || { echo "無法建立暫時 network"; exit 2; }
docker create --name "$STUB" --network "$NETWORK" --network-alias api "$IMAGE" >/dev/null || exit 2
copy_tree_into "$STUB" "$stage_stub" || exit 2
docker start "$STUB" >/dev/null || exit 2
docker create --name "$WEB" --network "$NETWORK" -p 127.0.0.1::8080 "$IMAGE" >/dev/null || exit 2
copy_tree_into "$WEB" "$stage_web" || exit 2
docker start "$WEB" >/dev/null || exit 2

port="$(docker port "$WEB" 8080/tcp | head -1 | sed 's/.*://')"
base="http://127.0.0.1:${port}"
ready=0
for _ in $(seq 1 30); do
  [ "$(curl -s -o /dev/null -w '%{http_code}' "$base/")" = "200" ] && { ready=1; break; }
  sleep 1
done
if [ "$ready" -ne 1 ]; then
  echo "web 容器未就緒，nginx 日誌："
  docker logs "$WEB" 2>&1 | tail -n 20
  exit 2
fi

# 依檔案大小取最大的 js／css，確保超過 gzip_min_length（1024）。
largest_asset() {
  docker exec "$WEB" sh -c "ls -S /usr/share/nginx/html/assets/*.$1 2>/dev/null | head -1" | tr -d '\r' | xargs -r basename
}
js_asset="$(largest_asset js)"
css_asset="$(largest_asset css)"
[ -n "$js_asset" ] && [ -n "$css_asset" ] || { echo "映像內找不到 assets 的 js／css"; exit 2; }

fetch_headers() { curl -s -o /dev/null -D - "$@" | tr -d '\r'; }
header_count() { printf '%s\n' "$1" | grep -ic "^$2:"; }
header_value() { printf '%s\n' "$1" | grep -i "^$2:" | head -1 | sed 's/^[^:]*: *//'; }
status_of() { printf '%s\n' "$1" | head -1 | awk '{print $2}'; }

assert_security_headers() {
  local label="$1" url="$2" expected_status="$3"
  shift 3
  local headers
  headers="$(fetch_headers "$@" "$url")"
  local status
  status="$(status_of "$headers")"
  if [ "$status" != "$expected_status" ]; then
    fail "$label" "$url 預期狀態 $expected_status，實際 $status"
    return
  fi
  local entry name expected count actual all_ok=1
  for entry in "${EXPECTED_HEADERS[@]}"; do
    name="${entry%%|*}"
    expected="${entry#*|}"
    count="$(header_count "$headers" "$name")"
    actual="$(header_value "$headers" "$name")"
    if [ "$count" -eq 0 ]; then
      fail "$label" "$url 缺少 $name"
      all_ok=0
    elif [ "$count" -ne 1 ]; then
      fail "$label" "$url 的 $name 出現 $count 次"
      all_ok=0
    elif [ "$actual" != "$expected" ]; then
      fail "$label" "$url 的 $name 值不符：預期 [$expected]，實際 [$actual]"
      all_ok=0
    fi
  done
  [ "$all_ok" -eq 1 ] && pass "$label" "$url（$status）五個安全標頭各一次且值相符"
}

# 實際映像由 Dockerfile 組出；本腳本以 docker cp 注入工作樹的設定，所以另以靜態斷言確認 Dockerfile 有複製 snippet。
if grep -qF "COPY --from=deploy nginx/security-headers.conf /$SNIPPET_TARGET" deploy/web.Dockerfile; then
  pass "ESH-001" "deploy/web.Dockerfile 複製 security-headers.conf 至 /$SNIPPET_TARGET"
else
  fail "ESH-001" "deploy/web.Dockerfile 沒有複製 security-headers.conf 至 /$SNIPPET_TARGET"
fi
for location in "/api/" "/assets/" "/"; do
  block="$(awk -v loc="    location $location {" '$0 == loc {p=1} p {print} p && /^    }/ {exit}' "$NGINX_DIR/default.conf" | tr -d '')"
  if printf '%s
' "$block" | grep -qF "include /$SNIPPET_TARGET;"; then
    pass "ESH-001" "default.conf 的 location $location 有 include security-headers.conf"
  else
    fail "ESH-001" "default.conf 的 location $location 沒有 include security-headers.conf"
  fi
done

# 安全標頭：ESH-001～005（CSP 逐字比對含在每個路徑的值比對內）
assert_security_headers "ESH-001/005" "$base/" 200
assert_security_headers "ESH-002/005" "$base/assets/$js_asset" 200
assert_security_headers "ESH-004" "$base/assets/does-not-exist.js" 404
assert_security_headers "ESH-003/005" "$base/api/ok" 200
assert_security_headers "ESH-003" "$base/api/client-error" 404
assert_security_headers "ESH-003" "$base/api/server-error" 500

asset_headers="$(fetch_headers "$base/assets/$js_asset")"
if [ "$(header_value "$asset_headers" Cache-Control)" = "public, max-age=31536000, immutable" ]; then
  pass "ESH-002" "靜態資源同時保有長期快取的 Cache-Control"
else
  fail "ESH-002" "靜態資源 Cache-Control 不符：$(header_value "$asset_headers" Cache-Control)"
fi

# HSTS：ESH-007、008
for path in "/assets/$js_asset" "/api/ok"; do
  headers="$(fetch_headers -H 'X-Forwarded-Proto: https' "$base$path")"
  count="$(header_count "$headers" Strict-Transport-Security)"
  value="$(header_value "$headers" Strict-Transport-Security)"
  if [ "$count" -eq 1 ] && [ "$value" = "max-age=15552000" ]; then
    pass "ESH-007" "$path 帶 X-Forwarded-Proto: https 時 HSTS 為 max-age=15552000 且只出現一次"
  else
    fail "ESH-007" "$path 的 HSTS 出現 $count 次、值 [$value]"
  fi
done
for forwarded in "X-Forwarded-Proto: http" ""; do
  for path in "/" "/api/ok"; do
    if [ -n "$forwarded" ]; then headers="$(fetch_headers -H "$forwarded" "$base$path")"; else headers="$(fetch_headers "$base$path")"; fi
    if [ "$(header_count "$headers" Strict-Transport-Security)" -eq 0 ]; then
      pass "ESH-008" "$path（${forwarded:-無 X-Forwarded-Proto}）沒有 HSTS"
    else
      fail "ESH-008" "$path（${forwarded:-無 X-Forwarded-Proto}）不應有 HSTS：$(header_value "$headers" Strict-Transport-Security)"
    fi
  done
done

# gzip：ESH-010～012
assert_gzip_round_trip() {
  local label="$1" asset="$2"
  local plain="$work_dir/plain.$asset" compressed="$work_dir/gz.$asset"
  curl -s -o "$work_native/plain.$asset" "$base/assets/$asset"
  local headers
  headers="$(curl -s -o "$work_native/gz.$asset" -D - -H 'Accept-Encoding: gzip' "$base/assets/$asset" | tr -d '\r')"
  if [ "$(header_value "$headers" Content-Encoding)" != "gzip" ]; then
    fail "$label" "$asset 未壓縮（Content-Encoding：[$(header_value "$headers" Content-Encoding)]，Content-Type：[$(header_value "$headers" Content-Type)]）"
  elif ! header_value "$headers" Vary | grep -qi 'Accept-Encoding'; then
    fail "$label" "$asset 缺少 Vary: Accept-Encoding"
  elif ! gzip -dc "$compressed" | cmp -s - "$plain"; then
    fail "$label" "$asset 解壓後與原檔不同"
  else
    pass "$label" "$asset 為 gzip、帶 Vary: Accept-Encoding，解壓後與原檔相同"
  fi
}
assert_gzip_round_trip "ESH-010(js)" "$js_asset"
assert_gzip_round_trip "ESH-010(css)" "$css_asset"

# ESH-011 固定用帶 Content-Length 的小型回應：chunked 回應不受 gzip_min_length 限制（design.md 決策 6）。
small_headers="$(fetch_headers -H 'Accept-Encoding: gzip' "$base/api/ok")"
if [ "$(header_count "$small_headers" Content-Length)" -ne 1 ]; then
  fail "ESH-011" "前提不成立：/api/ok 回應沒有 Content-Length"
elif [ "$(header_count "$small_headers" Content-Encoding)" -ne 0 ]; then
  fail "ESH-011" "小型回應不應壓縮：Content-Encoding [$(header_value "$small_headers" Content-Encoding)]"
else
  pass "ESH-011" "帶 Content-Length 的小型回應未壓縮"
fi

plain_headers="$(fetch_headers "$base/assets/$js_asset")"
if [ "$(header_count "$plain_headers" Content-Encoding)" -eq 0 ]; then
  pass "ESH-012" "未宣告 Accept-Encoding 時 $js_asset 不壓縮"
else
  fail "ESH-012" "未宣告 Accept-Encoding 卻被壓縮：[$(header_value "$plain_headers" Content-Encoding)]"
fi

# gzip_types 必須涵蓋實際的 JS／CSS Content-Type（映像內 mime.types 可能是 text/javascript 或 application/javascript）。
if docker exec "$WEB" nginx -t >/dev/null 2>&1; then
  pass "ESH-010" "nginx -t 通過"
else
  fail "ESH-010" "nginx -t 失敗：$(docker exec "$WEB" nginx -t 2>&1 | tail -n 5)"
fi
effective_config="$(docker exec "$WEB" nginx -T 2>/dev/null)"
for asset in "$js_asset" "$css_asset"; do
  content_type="$(header_value "$(fetch_headers "$base/assets/$asset")" Content-Type | sed 's/;.*//')"
  if printf '%s\n' "$effective_config" | grep -E '^\s*gzip_types' | grep -qF "$content_type"; then
    pass "ESH-010" "nginx -T 的 gzip_types 含 $asset 的 Content-Type（$content_type）"
  else
    fail "ESH-010" "nginx -T 的 gzip_types 不含 $asset 的 Content-Type（$content_type）"
  fi
done

echo
echo "結果：PASS $passed、FAIL $failed"
[ "$failed" -eq 0 ]
