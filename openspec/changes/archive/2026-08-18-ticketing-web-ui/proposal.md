## Why

售票平台目前只有後端 API（會員/售票核心/後台管理/購票/訂單管理皆已完成並合併至 master），沒有任何前端介面，所有功能只能透過 Swagger 或直接呼叫 API 測試。為了讓買家與 Admin 能真正使用這個系統、也讓後續金流整合等功能有實際的使用情境可以驗證，需要建立 Vue 3 前端專案，涵蓋買家端與 Admin 後台兩塊介面。

## What Changes

- 新增 Vue 3 前端專案（`web/`），Composition API + `<script setup>`，加入既有 Docker Compose 環境（新增 `web` service）
- 建立共用前端架構：路由（Vue Router）、API service 層（統一封裝既有後端 API、集中處理 Auth Header 與錯誤格式）、狀態管理（Pinia，含登入狀態/角色）、登入後的路由守衛（依角色導向買家端或 Admin 後台，非 Admin 不可進入後台路由）
- 買家端頁面：登入/註冊、活動列表、活動座位選擇與下單、下單結果頁（確認/取消剛下的訂單）
- Admin 後台頁面：登入導向後台、場館/座位圖管理、活動/票種管理、訂單列表與明細查看
- 本次僅涵蓋既有後端 API 已支援的操作；不新增或修改任何後端 API 行為——查證後發現買家專屬的「查詢我的訂單」API 不存在（僅有 Admin-only 版本），故「我的訂單」列表/明細頁面本輪只建立路由與空狀態畫面，標記待後端補上對應 API 後再串接（見 design.md）
- 同樣查證後發現 Admin 場館／座位圖／活動也沒有對應的查詢 API（只有建立用的 `POST`）；與使用者確認範圍後（2026-08-17），本輪維持完整買家＋Admin MVP 的範圍，但這部分改用「手動輸入 Id」頂著，不在本輪新增後端查詢 API，待下一個 OpenSpec 變更補齊後再換成下拉選單與正常列表（見 design.md Non-Goals）

## Capabilities

### New Capabilities
- `buyer-web-ui`: 買家透過瀏覽器完成登入、瀏覽活動與座位、下單、在下單結果頁確認／取消剛下的訂單（「我的訂單」列表/明細本輪僅為空狀態頁面，尚不能查看歷史訂單，見下一點與 design.md Non-Goals）
- `admin-web-ui`: Admin 透過瀏覽器完成登入、管理場館/座位圖/活動/票種、查看所有訂單

### Modified Capabilities
（無——本次為新增前端介面，不變更既有後端 API 的 Requirement）

## Impact

- 新增 `web/` 前端專案目錄與其 Dockerfile
- 修改 `docker-compose.yml`（新增 `web` service，設定其對 `api` 的 `depends_on` 與對外 port）
- 不影響現有 `src/`、`tests/` 下的後端程式碼與既有 API 行為，**兩個例外**（皆為使用者驗收階段直接要求，皆為純新增選填欄位/檢查，不改既有欄位或行為，見 design.md Non-Goals）：① 新增 `Event.Description`／`Event.PosterUrl`，供活動頁顯示海報/說明；② 新增 `Event.MaxTicketsPerOrder`，並在 `OrderService.PlaceOrderAsync` 加上超額下單檢查，供「每筆訂單限購張數」這條業務規則的後端把關（前端也有對應提示，但後端才是最終邊界）
- 依賴既有後端已完成的 API：`authentication`、`member-management`、`event-catalog`、`event-management`、`ticket-purchase`、`order-administration` 等 spec 所定義的端點
