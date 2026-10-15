# Design

## Context

動機見 proposal.md - Why，行為規則見 `specs/event-management/spec.md`。

- `CreateEventRequestValidator` 目前對 `StartAtUtc` 只檢查 `NotEqual(default)`。整微秒規則只在提供 `SalesStartAtUtc` 時才套用（EVT-SALES-018），本次不改。
- 販售時間欄位已有的做法（`event-sales-window` design.md 決策 2）：
  - Validator 用 `Kind == DateTimeKind.Utc` 擋非 UTC，用 `NotEqual(DateTime.MinValue)` 擋 `-infinity`。
  - Domain `Event` 建構子只檢查 Kind。
- 請求反序列化（2026-10-15 在 `api` 容器內以 .NET SDK 10.0.401、System.Text.Json 預設設定實測，容器時區 UTC）：
  - `2026-12-01T10:00:00Z` → `Kind=Utc`；`9999-12-31T23:59:59.9999999Z` → `Kind=Utc`（等於 `DateTime.MaxValue`）。
  - `2026-12-01T10:00:00` → `Kind=Unspecified`。
  - `+08:00`、`+00:00`、`-00:00` → 換算成本機時間，`Kind=Local`（偏移為零也一樣）。
  - 小寫 `z` → 反序列化直接失敗（`JsonException`），走模型繫結錯誤，不會到 Validator，也不會 500；不在本 change 範圍。
- Npgsql 沒有開 legacy timestamp 或關閉 infinity 轉換（已 grep 確認）。
  - 寫入 `timestamptz` 時遇到非 UTC 的 `DateTime` 會丟例外，這就是現在 500 的來源。
  - 讀回一般值時一律是 `Kind=Utc`。
  - `DateTime.MaxValue`／`MinValue` 會與 `infinity`／`-infinity` 互轉，讀回時 `Kind=Unspecified`。
- EF 透過 `Event` 的建構子具現化實體，所以建構子守衛在讀取時也會執行。

## Goals / Non-Goals

**Goals**
- 非 UTC 與最大值的 `StartAtUtc` 在 Validator 就擋成 400。
- Domain 有同樣的 Kind 守衛，維持 Rich Domain Model 的不變式：`Event` 自己保證 `StartAtUtc` 為 UTC，不依賴呼叫端先過 Validator。目前 `src` 內唯一建立 `Event` 的是 `CreateEventHandler`（前面已有 Validator），`DevelopmentDataSeeder` 不建立活動；守衛的實際作用是擋測試與日後新增的建立路徑，以及在 EF 讀取具現化時檢查資料。

**Non-Goals**
- `StartAtUtc` 的整微秒規則維持現狀（EVT-SALES-018 的相容承諾）。
- 不檢查 `StartAtUtc` 是否在過去或太遠的未來，這是業務規則，不在本次缺口範圍。
- 前端不變，因為它已經一律送 `toISOString()`。

## Decisions

### 決策 1：Validator 加兩條規則，比照販售欄位

`StartAtUtc` 加兩條規則：
- `Must(value => value.Kind == DateTimeKind.Utc)`
- `NotEqual(DateTime.MaxValue)`

錯誤訊息比照既有格式：
- Kind：`StartAtUtc must be a UTC time (with 'Z' designator).`
- 最大值：`StartAtUtc must not be the maximum date.`

錯誤回應沿用既有形狀：`CreateEventHandler` 把所有 Validator 訊息以空白串接成一個 `Error.Validation`，WebApi 輸出為 ProblemDetails（`Title=Validation`、訊息在 `detail`），沒有欄位鍵。測試斷言 `detail` 含上述完整訊息，與既有販售欄位測試相同。

`StartAtUtc` 為 `DateTime.MinValue`（`-infinity`）時等於 `default`，已被既有的 `NotEqual(default)` 擋下，Domain 也已檢查 `default`，所以不另加 MinValue 規則。

**契約是語意上的 UTC，不是字串字面檢查。** Validator 拿到的是反序列化後的 `DateTime`，看不到原始字串，所以 spec 定義為「反序列化後 `Kind` 必須為 `Utc`」。依上方實測，這在本系統等同「字串結尾為 `Z`」。spec 的 Scenario 把 `+00:00` 等字串列為規範性範例，測試會鎖住這個對應：若之後升級 runtime 讓 `+00:00` 變成 `Utc`，EVT-CREATE-007 的測試會失敗，屆時再決定是否改契約，不會靜默放行。

**不採用字面檢查**（保留原始字串或自訂 `JsonConverter` 要求結尾為 `Z`）：目前語意檢查與字面檢查的結果相同，多寫一層沒有實際收益；而且會讓 `StartAtUtc` 與販售欄位走兩套機制。

既有主 spec 對販售欄位寫的是「請求時間字串須帶 `Z`」（字面描述），與本 change 的語意描述在目前 runtime 下等價。本 change 只改 `StartAtUtc` 的描述，不改販售欄位。這裡記下差異，日後整理 spec 時可以把販售欄位的描述統一成語意版本。

Kind 規則加 `.When(x => x.StartAtUtc != default)`。未提供 `StartAtUtc` 時，原本的「必填」錯誤已足夠，不另外疊一條 UTC 錯誤。

**不採用**：在反序列化時用自訂 `JsonConverter` 拒絕非 UTC 字串。這樣會影響全站所有 `DateTime` 欄位，範圍過大；而且錯誤會變成模型繫結錯誤，走另一套錯誤回應路徑，與販售欄位經 Validator 回 `Title=Validation` 的行為不一致。

### 決策 2：Domain 只加 Kind 守衛，最大值只在 Validator 擋

`Event` 建構子加上 `if (startAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException(...)`，與 `salesStartAtUtc`／`salesEndAtUtc` 的守衛相同。

最大值規則不放 Domain，理由有兩點：

1. **它不是 Domain 不變式。** 對 `Event` 來說，「開始時間是 UTC」有業務意義：販售狀態要用 UTC 比較。`DateTime.MaxValue` 則是合法的 UTC 值，Domain 沒有理由拒絕它。會出問題是因為 Npgsql 把它當成 PostgreSQL `infinity` 的 sentinel，寫入後讀回 `Kind=Unspecified`。這是儲存技術的限制，換掉資料庫或關閉 infinity 轉換就不存在。把它寫進 Domain，等於讓 Domain 依賴持久化細節，違反 Domain 不依賴其他層的原則。
2. **比照既有做法。** 販售欄位的 `MinValue`（`-infinity` sentinel）規則也只在 Validator。

這兩種做法互相衝突時，選擇沿用既有模式，不混用（CLAUDE.md Rule 7）。若之後要把持久化限制下放到 Domain，販售欄位要一起改。

**風險**：Domain 守衛在讀取時也會執行，見 Risks 第 1 點。

### 決策 3：部署前資料閘門

**適用範圍與負責人**：部署本 change 到任何資料庫環境之前，由執行部署的人（目前是專案唯一維護者）對該環境執行檢查。目前唯一的環境是本機 Docker Compose 開發環境；雲端部署尚未決定（`docs/project-scope.md` §4）。之後若新增環境，首次部署含本 change 的版本前也要照同樣流程執行。

**步驟 1：檢查。** 帳密取自 `db` 容器的環境變數（來源為 `.env`，經 compose 注入），用容器內 local socket 連線，不需另外輸入密碼：

```sh
docker compose exec -T db sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -v ON_ERROR_STOP=1 -tAc "SELECT count(*) FROM \"Events\" WHERE NOT isfinite(\"StartAtUtc\") OR NOT isfinite(\"SalesStartAtUtc\") OR NOT isfinite(\"SalesEndAtUtc\") OR \"SalesEndAtUtc\" > \"StartAtUtc\" OR \"SalesStartAtUtc\" >= COALESCE(\"SalesEndAtUtc\", \"StartAtUtc\");"'
```

這個查詢計算「讀取時會讓 `Event` 建構子丟例外」的資料列，不只看 `StartAtUtc`：
- 三個時間欄位任一為 `±infinity`（讀回 `Kind=Unspecified`，觸發 Kind 守衛；`NULL` 不算）。
- 停售晚於開始，或開賣不早於有效停售時間（建構子既有的販售區間守衛）。

輸出必須是單一數字。指令失敗或沒有輸出，都視為檢查未完成，不得部署。

**步驟 2：判定。** 結果為 `0` 才可部署該環境；不為 0 時**禁止部署該環境**，先做步驟 3。每個環境各自判定，其他環境（包括開發環境）通過不能取代。

**步驟 3：修復**（僅在不為 0 時執行，且在部署新版之前執行；舊版讀取修復後的資料也正常）：

- 先確認可能的來源：`-infinity` 等於 `default`，現有 Validator 一直都擋得住，正常情況不會出現；`infinity` 只會來自以 `DateTime.MaxValue` 建立、且未設定 `SalesStartAtUtc` 的活動（設定了開賣時間時，整微秒規則會擋下 `MaxValue`）。
- 優先做法：負責人向主辦方確認真實開始時間，以 UTC 寫回。新值必須是整微秒，且 ≥ `SalesEndAtUtc`（若有設定）。
- 先用步驟 1 的條件改成 `SELECT "Id", "StartAtUtc", "SalesStartAtUtc", "SalesEndAtUtc"` 列出問題資料列，確認資料形狀後再選修復方式。
- 無法確認時，下面是**只適用於「`StartAtUtc` 為 infinity、販售欄位皆為有限值或 NULL」這種形狀的修復範例**，不保證所有資料形狀都能一次修好。其他形狀（例如販售欄位也是 infinity）須由負責人個別決定修法。範例做法：把開始時間設為「現在」與 `SalesEndAtUtc` 中較晚的一個。活動會立即停售並顯示為已開始，但訂單、座位等關聯資料都保留，不刪除活動：

```sql
UPDATE "Events"
SET "StartAtUtc" = GREATEST(date_trunc('second', now()), "SalesEndAtUtc")
WHERE NOT isfinite("StartAtUtc")
  AND (isfinite("SalesStartAtUtc") OR "SalesStartAtUtc" IS NULL)
  AND (isfinite("SalesEndAtUtc") OR "SalesEndAtUtc" IS NULL);
```

**步驟 4：驗證。** 在同一環境重新執行步驟 1，結果必須為 0（同時涵蓋販售欄位，不只表面的 `StartAtUtc`）。部署後，用受影響活動（若有）驗證以下三個端點都回 200：`GET /api/events`、該活動所屬 Organizer 的 `GET /api/admin/events`、`GET /api/events/{id}/seats`。公開列表有查詢快取，TTL 內可能掩蓋 500；因此列表正常不算證據，以步驟 1 的 SQL 與 `/seats`（不經快取）為準。

**紀錄**：每次執行都記錄環境名稱、執行時間、指令輸出，以及是否做過修復。

2026-10-15 已在開發 DB 執行步驟 1 的指令，結果為 0。這只是實作前的本機驗證，不能取代部署當下的檢查。

`timestamptz` 的一般值由 Npgsql 讀回時一定是 `Kind=Utc`，所以只有 infinity 會觸發守衛。整合測試 EVT-CREATE-009 驗證，加上守衛後讀回一般 UTC 活動，公開列表、公開座位查詢、後台列表三個端點都正常（公開活動沒有單筆詳情端點；座位查詢會以 `GetByIdAsync` 讀取 `Event`）。

## 安全確認

本 change 涉及外部輸入（建立活動 API 的 `StartAtUtc`）與資料庫寫入，依 CLAUDE.md 安全強制規則逐條回答：

- **輸入驗證**：在 Application 層 `CreateEventRequestValidator`（FluentValidation），由 `CreateEventHandler` 在建立實體前呼叫；Domain 建構子再守一次 Kind。
- **SQL／shell 拼接**：無。應用程式碼不新增任何查詢；寫入沿用既有 EF Core `Add` + `SaveChanges`（參數化）。決策 3 的 SQL 是部署前人工執行的維運查詢，不進應用程式碼、不含外部輸入。
- **N+1**：不新增查詢，無新增風險。
- **權限**：沿用既有建立活動端點的授權（須已切換至 Approved Organizer，`OrganizerId` 取自 JWT），本次不改；未授權使用者在授權階段即被拒，碰不到新規則。
- **前端**：不變；不新增 DOM 渲染，Auth Header 由既有攔截器帶入。

## Risks / Trade-offs

- **[讀取時守衛丟例外] → 上線前檢查。** 若資料庫已有 `StartAtUtc = infinity` 的活動（過去用 `DateTime.MaxValue` 建立成功的），加上守衛後，任何讀到這筆的查詢都會 500，包括公開活動列表。處理方式：依決策 3 的部署前資料閘門執行；筆數不為 0 時禁止部署，修復並驗證後才可部署。
- **[測試 fixture 用了非 UTC 時間] → 實作時一次修完。** 直接 `new Event(...)` 的測試有 32 個檔。2026-10-15 已 grep 未指定 Kind 的 `new DateTime(...)`、`DateTime.Today`、`DateTime.Parse`、`DateTime.Now`，測試內皆無，預期影響很小；但仍可能有其他來源，若任何一個用了 `Kind=Unspecified`／`Local`（例如未指定 Kind 的 `new DateTime(...)`、`DateTime.Now`），加上守衛後會失敗。處理方式：加守衛後跑全部後端測試，逐一把 fixture 改成 UTC；這屬於測試資料修正，不改測試意圖。
- **[直接呼叫 API 的客戶端行為改變]** 原本以 `DateTime.MaxValue` 建立成功的請求改回 400。這是錯誤輸入，接受此行為改變。

## Migration Plan

- 沒有 schema 遷移。
- 部署前依決策 3 執行資料閘門，結果須為 0 筆，否則禁止部署。
- 回滾：還原程式碼即可。若曾執行步驟 3 的修復，修復後的資料是合法 UTC 值，舊版也能正常讀取，不需要還原資料。
