## Context

核銷 API（`PATCH /api/admin/tickets/{id}/redeem`，`ticket-redemption` 能力）已上線，僅接受 Admin 角色呼叫，回傳 204（成功）／409（狀態衝突，如已核銷過）／404（不存在或路徑非合法 GUID）。QR Code 內容格式原僅存在於已封存的 `ticket-issuance-and-redemption` design.md 決策 3（`{TicketId:D}.{Base64Url(HMAC-SHA256(TicketId, key))}`），本次一併把這個精確格式契約同步進權威的 `ticket-issuance` spec（見本次 `specs/ticket-issuance/spec.md` 的 MODIFIED Requirement），供前端 parser／後端驗證雙方對齊，不再只存在於已封存文件。驗章邏輯 `Domain.Tickets.ITicketSigningService.TryVerify(string? content, out Guid ticketId)` 已實作（`Infrastructure/Tickets/HmacTicketSigningService.cs`）、已單元測試、已在 `Program.cs` 註冊 DI，但**目前系統中沒有任何呼叫路徑使用它**——原始設計文件明確把這件事列為留給「現場掃碼前端」（即本次變更）整合的項目。

錯誤回應現況：`src/ProjectC.WebApi/Common/ResultExtensions.cs` 的 `CreateProblemResult` 把 `ProblemDetails.Title` 設為 `error.Type.ToString()`（`ErrorType` 列舉值本身），HTTP status 只反映錯誤大類（400/401/403/404/409）。專案已有前例：`rate-limiting-queue` 的 `ErrorType.QueueAdmissionRequired` 同樣對映 403，前端用 `error.status === 403 && error.problem?.title === 'QueueAdmissionRequired'`（`web/src/pages/buyer/EventDetailPage.vue:370`）與其他 403 區分，測試見 `EventDetailPage.test.ts` 的 BW-QUEUE-004/006。本次簽章驗證失敗比照此既有慣例，見決策 2。

前端現況：
- `web/src/router/index.ts` 的 `/admin/*` 巢狀路由已有共用角色守衛，新增子路由不需重寫守衛邏輯
- `web/src/api/httpClient.ts` 的 `RequestOptions.method` 目前僅列 `'GET' | 'POST' | 'PUT' | 'DELETE'`，缺少 `'PATCH'`——呼叫核銷端點前需先補上
- `web/src/types/api.generated.ts` 未涵蓋核銷端點（生成時間早於該端點上線），本次比照既有 `web/src/api/admin.ts` 手動撰寫呼叫函式與型別，不依賴自動產生
- `package.json` 目前的 `dependencies` 僅有 Vue 生態系套件（`vue`／`vue-router`／`pinia`／`element-plus`），沒有任何 QR/Barcode 解碼相關依賴
- `web/src/layouts/AdminLayout.vue` 目前導覽選單只有場館管理／活動管理／訂單管理三項（水平 `el-menu`），沒有核銷入口

**瀏覽器相容性事實查核**：原先假設「Chrome / Safari / Edge 最近 2 個大版本皆支援 `BarcodeDetector`」，經查證 MDN（該 API 明確標示 *Not Baseline*）與 caniuse，**Safari（含 iOS Safari）完全不支援 `BarcodeDetector`**，Firefox 亦不支援。`docs/project-scope.md` 已將 Safari 列入正式支援瀏覽器下限，此事實推翻了原先「用原生 API」的決策，見決策 1。

**依賴授權查核**：`jsqr`（決策 1 採用的解碼函式庫）經查證 npm 與 GitHub（`cozmo/jsQR`）皆標示為 **Apache-2.0**，不是 MIT；本文件先前版本誤植為 MIT，已修正。

## Goals / Non-Goals

**Goals:**
- Admin 可用手機/平板瀏覽器（含 iOS Safari）相機掃描票券 QR Code 並完成核銷
- 掃描到的 QR 簽章內容由後端驗證，維持 QR 防偽設計的實際效力（見決策 2）
- 掃描結果（成功／已核銷過／查無此票／簽章無效／無法辨識／系統錯誤）即時、可分辨地顯示，不僅依賴顏色
- 相機掃描期間，使用者可隨時主動切換到手動輸入（例如 QR 毀損時），不需等待相機判定失敗
- 相機完全不可用時（權限拒絕、無相機裝置、不支援）自動落到手動輸入
- 連續掃描下一張票不需重新整理頁面或手動導覽；因系統錯誤失敗的票券可以立即重新嘗試，不被誤判為重複掃描而擋下
- Admin 後台導覽有明確的核銷頁面入口
- UI 上明確區分「掃描核銷」（有簽章驗證）與「手動輸入」（Admin 信任操作、無簽章驗證）兩種操作的性質，避免操作人員誤以為兩者提供相同的真偽保證

**Non-Goals:**
- 手動輸入路徑的來源鑑別——手動輸入只接受 Ticket ID（無簽章可附帶），沿用「Admin 角色本身即信任邊界」的既有模型（見決策 2 的信任邊界說明），不因為這次加了掃描路徑的簽章驗證，就反過來要求手動輸入也要提供簽章
- 新增獨立的檢票人員角色或核銷權限範圍限制（沿用既有 Admin-only）
- 現場離線（無網路）核銷——核銷本質是連線中的 API 呼叫，離線情境不在範疇內；這也是 QR 簽章原始設計文件所述其主要防偽價值場景（離線判斷 QR 是否為系統產出），本次不實現該離線場景，但仍在「連線中」場景下把已存在的驗章能力接上，見決策 2
- 掃描歷程記錄／統計頁面
- 用 `ProblemDetails.type`（URI 分類）取代 `Title` 作為錯誤判別依據——維持專案既有的 `Title = ErrorType.ToString()` 慣例（見 Context），不引入新的錯誤分類機制

## Decisions

### 決策 1：QR/Barcode 解碼改採跨瀏覽器 canvas-based 函式庫（`jsqr`，Apache-2.0 授權），不使用原生 `BarcodeDetector`；解碼頻率節流，不逐幀解碼
新增 npm 依賴 `jsqr`（純 JS、僅對 `ImageData` 做解碼運算、無框架相依，**Apache-2.0 授權**）。實作方式：`getUserMedia({ video: { facingMode: 'environment' } })` 取得後鏡頭串流餵給隱藏的 `<video autoplay muted playsinline>`（`playsinline` 為 iOS Safari 內嵌播放必要屬性，否則會被強制全螢幕播放）。

**解碼頻率節流**：偵測迴圈仍以 `requestAnimationFrame` 驅動生命週期（跟著螢幕更新率、分頁背景時瀏覽器自動降頻/停止，行為比 `setInterval` 更省電），但實際呼叫 `jsQR` 解碼的頻率節流在約 10–15 次/秒（用時間戳記比對，未達間隔的 frame 直接略過，不執行 canvas draw 與解碼）；`video.readyState < HAVE_CURRENT_DATA` 或 `video.videoWidth === 0` 時同樣略過，不對空畫面解碼；`<canvas>` 與其 `ImageData` buffer 只在初始化時建立一次並重複使用，不逐幀重新配置；`processing`／`result` 狀態下完全不執行解碼（迴圈可以繼續跑或直接暫停，但不呼叫 `jsQR`）。

啟動前先做能力偵測，任一條件不成立就直接進入手動輸入模式（見決策 4 的狀態機，不嘗試初始化相機）：
- `window.isSecureContext`（HTTPS，`getUserMedia` 前提；`localhost` 例外）
- `navigator.mediaDevices?.getUserMedia` 存在

**後鏡頭 constraint 的處理範圍（刻意限制，非疏漏）**：只嘗試 `facingMode: 'environment'` 一組 constraint，不做「失敗後退回 `video: true` 再試一次」的 fallback。`OverconstrainedError`（裝置無法滿足此 constraint，例如無後鏡頭的裝置）與 `NotFoundError` 一併對應決策 4 的 `camera-unavailable` 狀態，直接落到手動輸入；`NotAllowedError`（使用者拒絕權限）獨立對應 `permission-denied`，不因為 constraint 也失敗而混淆成同一種狀態。現場核銷的目標裝置（手機/平板）絕大多數有後鏡頭，為這個低機率情況新增第二次 `getUserMedia` 嘗試會讓決策 4 的狀態機與例外分類更複雜，不符合 CLAUDE.md Rule 2（簡潔優先）；手動輸入已是完整可用的備援路徑。

**理由**：`jsqr` 直接對畫面 pixel 資料解碼，不依賴瀏覽器原生 Shape Detection API，Chrome／Safari／Edge／Firefox 行為一致，涵蓋 `docs/project-scope.md` 承諾的完整瀏覽器矩陣，尤其是現場最可能使用的 iOS Safari；解碼節流避免逐幀（約 60 次/秒）執行完整 QR 解碼在手機上造成的 CPU 使用率、電池消耗與發熱問題，10–15 次/秒對「掃描到反應」的現場操作體驗而言已經足夠靈敏。

**替代方案（不採用）**：
- 原生 `BarcodeDetector`——免依賴，但 Safari 完全不支援，會讓 Goal「Admin 可用手機/平板瀏覽器相機掃描」在 iOS 裝置上形同虛設，不採用
- `@zxing/browser`——功能更完整，但本次只需要 QR 一種格式，且決策 4 要求自行掌控相機生命週期的每個環節，交給函式庫的內建迴圈反而要另外包一層才能滿足生命週期需求，選最小可用的 `jsqr` 更符合 CLAUDE.md Rule 2（簡潔優先）
- 逐幀（每個 `requestAnimationFrame`）都解碼——反應最即時，但手機上的效能/電力成本不成比例，不採用
- `facingMode` constraint 失敗時自動退回 `video: true` 再試一次——涵蓋無後鏡頭裝置，但增加狀態機分支複雜度，換來的實際需求覆蓋率低，不採用（見上方說明）

### 決策 2：核銷 API 新增可選的簽章驗證欄位，錯誤以既有 `ErrorType`／`Title` 慣例區分；前端格式檢查不構成安全邊界
`PATCH /api/admin/tickets/{id}/redeem` 新增可選 request body：`{ "signature": string | null }`。

後端行為（`RedeemTicketHandler`，Application 層，新注入 `ITicketSigningService`）：
- `signature` 為 `null`（手動輸入路徑，或任何未升級的舊呼叫端；request body 整個省略時 `[FromBody]` 模型繫結同樣得到 `null`）：行為與現況完全相同，不驗章，直接以資料庫狀態為權威來源核銷
- `signature` 為非 `null` 的字串（掃描路徑）：**不做額外的空字串/空白特殊判斷**，一律在取得資料庫鎖定之前，先重組 `{id:D}.{signature}` 呼叫 `ITicketSigningService.TryVerify`；空字串／空白字元會被 `TryVerify` 依既有 contract 自然判定為驗證失敗（見下方「回應審查意見」段落），不需要在 `RedeemTicketHandler` 另寫特殊分支——這是刻意選擇「一路呼叫既有方法，讓它自己安全拒絕」而非「先手動檢查空白再決定要不要呼叫」，兩者結果一致但前者不多一條分支路徑
- `signature` 欄位型別不符（例如 JSON 傳數字）：屬於 `RedeemTicketRequest` 反序列化失敗，`[ApiController]` 屬性已提供的自動模型驗證會在進入 Action 前直接回傳 400（ASP.NET Core 內建行為，非本次新增程式碼），同樣不會查詢或變更 Ticket 狀態，不需要額外處理
- 驗證失敗（含空字串／空白／竄改）回傳新的 `ErrorType.InvalidTicketSignature`（`Error.InvalidTicketSignature(message)`，比照既有 `ErrorType.QueueAdmissionRequired` 的新增方式），對映 HTTP 400，`ProblemDetails.Title` 固定為 `"InvalidTicketSignature"`，MUST NOT 查詢或變更任何 Ticket 的狀態；驗證通過才進入既有的鎖定→狀態檢查→核銷流程
- Log 規範：任何處理過程中的 log（若有）MUST NOT 輸出 `signature` 欄位值或重組後的完整內容，比照 CLAUDE.md 機敏資訊管理規則（雖然簽章本身不是密碼等傳統機敏資訊，但仍是特定票券的密碼學憑證，不需要的情況下不記錄）

**用固定 `Title` 字串而非僅靠 HTTP 400 狀態碼區分的原因**：`CreateProblemResult`（見 Context）目前把任何 `ErrorType.Validation` 都對映到 400；如果簽章驗證失敗也回傳通用 `ErrorType.Validation`，未來這個端點若新增其他 400 來源的驗證錯誤，前端會把所有 400 都誤判為「簽章無效」。新增專屬 `ErrorType.InvalidTicketSignature` 讓 `Title` 成為穩定判別依據，前端據此（而非泛用 400）判斷是否顯示「簽章驗證失敗」，比照 `web/src/pages/buyer/EventDetailPage.vue:370` 對 `QueueAdmissionRequired` 的既有寫法（`error.status === 400 && error.problem?.title === 'InvalidTicketSignature'`）。

前端行為：
- 掃描路徑：QR 內容格式為 `{TicketId}.{signature}`，取 `.` 後段作為 `signature` 一併送出（見決策 5 的 parser 分工）
- 手動輸入路徑：只接受純 Ticket ID，`signature` 固定為 `null`，不要求使用者輸入或偽造簽章

**前端格式驗證的角色（回應審查意見）**：前端 parser（決策 5）對掃描內容與手動輸入做的格式檢查，目的僅是避免明顯無效的內容浪費一次 API 呼叫、提早給使用者訊息，**不構成安全驗證邊界**；後端 `ITicketSigningService.TryVerify` 才是唯一可信的簽章驗證邊界，即使前端因為 bug 或惡意繞過送出未預期格式的內容，後端也必須安全拒絕（`TryVerify` 本身的 contract 已保證：任何 `null`／空字串／格式不符的輸入回傳 `false`，不拋例外，見 `ITicketSigningService.TryVerify` 的 XML doc）。

**信任邊界明確記錄**：掃描路徑因為附帶簽章，取得了「內容確實是本系統簽出、未被竄改」的密碼學保證；手動輸入路徑沒有這層保證，等同於既有系統一直以來就有的能力——任何 Admin 只要知道合法的 Ticket ID（不論從何得知）就能呼叫核銷 API，這不是本次變更新增的攻擊面，而是 `ticket-issuance-and-redemption` 上線時就存在、且已在該次 design.md 決策 3 承認並接受的既有行為。本次選擇不因為要「補強」而反過來限制或拿掉手動輸入路徑，因為那是相機不可用或 QR 毀損時的必要備援，兩條路徑分屬不同信任等級是刻意設計，不是疏漏——UI 上必須讓操作者看得出這個差異（見決策 6）。

**理由**：`ITicketSigningService.TryVerify` 已存在、已測試、已被原始設計文件保留給這次整合，成本低（新增一個 nullable 欄位＋一個新 `ErrorType`＋一段驗證分支，不改變既有無簽章呼叫端的行為，不需要 DB schema 異動）；讓掃描路徑真正用上 QR 防偽設計的密碼學保證，而不是讓 `ITicketSigningService` 停留在「沒有任何呼叫路徑使用」的狀態。

**替代方案（不採用）**：
- 完全不驗章（原決策）——放棄了免費可用、已測試好的防偽機制，不採用
- 簽章驗證失敗沿用通用 `ErrorType.Validation`——與其他 400 驗證錯誤無法區分，前端只能靠「這個端點目前唯一的 400 來源」這種脆弱假設判斷，不符合既有 `Title` 判別慣例，不採用
- 新增獨立端點（`{ "qrContent": string }`，後端自己從 `TryVerify` 解析出 Ticket ID）——可以讓前端完全不用自己 parse GUID，但需要新路由、既有呼叫端需遷移；在既有 `{id:guid}/redeem` 端點上疊加可選欄位維持相容成本更低

### 決策 3：核銷結果顯示——依結果類型調整停留時間與 ARIA 緊急程度，不僅用顏色區分
結果橫幅同時包含文字說明與圖示（不只顏色）。停留時間抽成常數，不寫死在元件邏輯中：
- **成功**：短暫顯示（預設 1.5 秒）後自動恢復掃描狀態，維持現場操作吞吐量；`role="status"` `aria-live="polite"`
- **已核銷過**：停留時間拉長（預設 4 秒），提供「立即繼續掃描」按鈕；`role="status"` `aria-live="polite"`（可預期的業務狀態，非緊急錯誤）
- **查無此票／簽章無效／無法辨識／系統錯誤**：停留時間拉長（預設 4 秒），提供「立即繼續掃描」按鈕；`role="alert"` `aria-live="assertive"`，且結果顯示時將鍵盤/螢幕報讀器焦點移至結果橫幅（`element.focus()` 搭配 `tabindex="-1"`），避免螢幕報讀器使用者錯過需要留意的錯誤

**理由**：直接回應「1.5 秒太短、只用顏色區分、`aria-live="polite"` 對錯誤可能不夠」的可用性疑慮；秒數常數化方便日後依現場實測回饋調整；區分 polite／assertive 讓可預期的業務結果（成功、已核銷過）不打斷操作節奏，但需要留意的錯誤會主動搶佔報讀順序。

### 決策 4：明確定義相機生命週期與狀態機，含背景/前景切換的完整恢復流程
狀態機（`initializing`／`scanning`／`processing`／`result`／`camera-unavailable`／`permission-denied`／`unsupported`／`error`）：

```
mounted
  ↓
initializing（決策 1 的能力偵測 + 嘗試 getUserMedia）
  ├─ 不支援（secure context / getUserMedia 缺任一項）→ unsupported → 手動輸入
  ├─ 使用者拒絕權限 → permission-denied → 手動輸入
  ├─ 無相機裝置 / getUserMedia 例外 → camera-unavailable → 手動輸入
  └─ 成功 → scanning
                ↓（jsQR 偵測到內容，或使用者按「改用手動輸入」，見決策 6）
            processing（呼叫核銷 API）
                ↓
             result（決策 3 的橫幅，依結果類型停留不同秒數）
                ↓（自動或手動恢復，清除本輪 dedupe 記憶，見決策 7）
             scanning
```

**背景/前景切換（`document.visibilitychange`）**：
- `hidden`：無論目前處於哪個狀態，一律停止 `requestAnimationFrame` 偵測迴圈、對 stream 的每個 `MediaStreamTrack` 呼叫 `stop()`、清除 `<video>.srcObject`，並記錄「切到背景前的狀態」；若切到背景當下正處於 `processing`（核銷呼叫進行中），SHALL 讓該次呼叫自然完成並記錄其結果（不中途取消），只是不再嘗試恢復相機直到重新可見；若當下正處於 `result`，停止倒數計時器但保留已顯示的結果內容
- `visible`：
  - 若元件已 unmount，MUST NOT 重新初始化（visibilitychange 監聽器需在 unmount 時一併移除，避免 memory leak 或對已卸載元件操作）
  - 若切背景前正在 `processing`：等待該次呼叫完成後，依其結果進入 `result`（不重新發送核銷請求）
  - 若切背景前正在 `result`：恢復顯示原本結果與（視情況重啟的）倒數，結果顯示完後才重新初始化相機進入 `scanning`，不在結果還沒被看到前就急著恢復掃描
  - 若切背景前為 `scanning`：重新執行一次 `initializing`（重新呼叫 `getUserMedia`）
  - 若切背景前為 `unsupported`：MUST NOT 自動重試——`window.isSecureContext`／`getUserMedia` 是否存在不會因為切到背景又切回來而改變，自動重試沒有意義，維持顯示手動輸入
  - 若切背景前為 `permission-denied`、`camera-unavailable` 或 `error`：MUST NOT 自動重試，維持顯示手動輸入表單；改為顯示一顆「重新嘗試相機」按鈕，只有使用者主動點擊才重新執行 `initializing`（若權限已在背景期間被使用者從瀏覽器設定撤銷，重新執行時 `getUserMedia` 會再次拋出例外，依例外分類重新落到 `permission-denied`）

**Race condition／過期非同步初始化的取消保護**：每次進入 `initializing`（無論是 mounted 後首次進入，或使用者點擊「重新嘗試相機」再次進入）都遞增一個內部 generation 計數器並記住當下的值；若在 `getUserMedia()` 這個 Promise 尚未 resolve/reject 前，使用者已切到背景（`hidden`）或元件已 unmount，一律讓 generation 計數器再遞增一次。`getUserMedia()` 完成時，先比對「呼叫當下記住的 generation」是否仍等於目前最新的 generation：不相等（代表這次初始化已經過期）時，立即對剛取得的 stream 呼叫 `stop()` 停止所有 track、直接捨棄，MUST NOT 掛到 `<video>.srcObject`、MUST NOT 啟動偵測迴圈、MUST NOT 更動元件狀態；相等時才照正常流程繼續（掛 stream、進入 `scanning`）。同一份保護機制同時涵蓋「初始化中途 unmount」與「初始化中途切背景，Promise 才完成」兩種時序。

**任何時間點只允許一份 stream／一個偵測迴圈存在**，重新初始化前必先完整停止前一份（避免權限對話框重複跳出或多個迴圈疊加）。`jsQR` 解碼本身是同步純函式不拋例外；`getUserMedia` 的例外（`NotAllowedError`／`NotFoundError`或`OverconstrainedError`／其他）分別對應 `permission-denied`／`camera-unavailable`／`error`（決策 1 的後鏡頭 constraint 失敗屬於 `OverconstrainedError`，歸類到 `camera-unavailable`）。

**非 404／409／400（`InvalidTicketSignature`）的 API 錯誤（網路中斷、5xx、401 換發後仍失敗等）SHALL 顯示可重試的通用系統錯誤（`error` 狀態），MUST NOT 顯示為「查無此票」或任何暗示票券本身有問題的訊息，且 MUST NOT 自動重試呼叫核銷 API**（見決策 3 的「立即繼續掃描」按鈕，由使用者主動觸發重試，而非背景自動重試造成非預期的重複呼叫）。

**理由**：
- 「沒有相機生命週期收尾」「切回前景後沒有恢復流程」——規則涵蓋 unmount／背景切換／權限被中途撤銷／處理中被切背景等邊界情況，避免記憶體洩漏、相機燈常亮，以及「畫面顯示 scanning 但實際上沒有 stream」的不一致狀態
- 「`permission-denied`／`camera-unavailable`／`unsupported` 不該自動重試」——使用者已明確拒絕過權限或裝置本來就不支援，切背景再切回來就自動重新彈權限對話框，觀感上是頁面在騷擾使用者；改成需要使用者主動點擊才重試，行為對操作者更可預期
- 「非同步初始化在背景切換後才完成」——這是相機類頁面常見的 race condition：`getUserMedia()` 是非同步的，使用者可能在它 resolve 前就已經切走或關閉頁面；generation 計數器保護避免「已經決定要停止相機了，卻因為一個較晚完成的 Promise 又重新掛上一份新 stream／偵測迴圈」

### 決策 5：QR 掃描內容與手動輸入分成兩個獨立的解析函式
- `parseTicketIdFromQrContent(content: string)`：驗證內容恰好含一個 `.` 分隔符、前段為合法 `D` 格式 GUID（不分大小寫皆接受，內部正規化）、後段（簽章）非空字串；三者皆符合才回傳 `{ ticketId, signature }`，否則回傳「無法辨識」錯誤，不呼叫 API。此函式的解析結果同時提供決策 2 所需的 `signature`。
- `parseTicketIdFromManualInput(value: string)`：只接受單一合法 GUID（允許前後空白，內部 trim；不接受任何 `.` 分隔符或附加內容），不合法時前端直接顯示「Ticket ID 格式不正確」並阻擋送出，不呼叫 API——不得讓格式錯誤流到後端變成 404 才顯示「查無此票」，兩種語意不同

兩個函式皆為決策 2 所述「僅避免無效 API 呼叫的前端檢查」，不構成安全邊界。

**理由**：直接回應「解析規則太寬鬆」「手動輸入不該被要求附帶簽章」的疑慮；兩個函式各自對應各自的資料來源與信任等級（決策 2），互不混用。

### 決策 6：手動輸入入口在掃描狀態下也一律可用，不只在相機完全不可用時才出現
`scanning` 狀態下，畫面除了相機預覽外，SHALL 同時提供一顆常駐的「改用手動輸入」按鈕，點選後直接切到手動輸入表單（不需要先讓相機判定失敗）；`camera-unavailable`／`permission-denied`／`unsupported`／`error` 四個狀態則直接以手動輸入表單為畫面主體（無相機預覽可顯示）——回應審查意見：`error`（`getUserMedia` 拋出非 `NotAllowedError`/`NotFoundError`/`OverconstrainedError` 的其他例外，或初始化過程其他非預期失敗）先前遺漏未歸類，比照 `camera-unavailable`/`permission-denied` 同樣視為「相機不可用」的一種，落到手動輸入，不讓操作者卡在無法核銷的畫面。`unsupported` 不提供「重新嘗試相機」（secure context／API 支援與否不會改變）；`camera-unavailable`／`permission-denied`／`error` 三者提供該按鈕（見決策 4 的 `visible` 規則）。

**理由**：直接回應「手動輸入只在相機不可用時出現，QR 毀損時無法主動切換」的缺口——相機運作正常但單一 QR 印刷模糊/破損是現場常見情境，操作者不應該被迫等待相機判定失敗才能改用手動輸入。

### 決策 7：重複偵測抑制只在 `processing`／`result` 期間有效，恢復 `scanning` 即清除記憶
「與上一次已送出核銷的內容是否相同、相同則忽略」的比對，記憶範圍僅限**當前這一輪** `processing` 到 `result` 結束為止；一旦（自動或手動）恢復到 `scanning` 狀態，這份記憶立即清除，不做任何跨輪次的持久化比對。

這代表：
- 因系統錯誤（非 404/409/400）失敗後，使用者按「立即繼續掃描」或等待自動恢復，同一張票可以立即重新嘗試，不會被「內容與上次相同」擋下
- 已核銷過／查無此票的票券，恢復掃描後若同一張票仍停留在鏡頭前，會被視為新一輪掃描重新呼叫一次核銷 API——結果與上次相同（409/404），是安全但略為多餘的一次呼叫，不造成資料錯誤（核銷本身已有既有的併發防重複機制），優先選擇「不永久阻塞合法重試」而接受這個小成本

**理由**：直接回應「dedupe 記憶沒有清除條件、可能永久阻塞合法重試」的疑慮；抑制的真正目的只是避免同一次結果顯示期間，鏡頭殘留同一張 QR 造成的重複呼叫，不是要記住「歷史上掃過什麼」。

## Risks / Trade-offs

- **[風險] jsdom 測試環境沒有 `getUserMedia`／相機／`jsQR` 真實影像輸入，單元測試無法涵蓋真實相機掃描到解碼成功這一段** → 緩解：把「取得一次掃描結果（含 `signature`）」抽成可注入/可 mock 的介面，單元測試針對決策 5 的兩個 parser、決策 2 的核銷呼叫與結果對應、決策 4 的狀態轉換（含背景/前景切換、unmount 收尾）做覆蓋；真實相機掃描到解碼成功的完整路徑需在實作完成後用實機做人工驗證，見 tasks.md
- **[風險] 真機（手機/平板）測試需要 secure context，開發環境目前是純 HTTP（`web` 容器對外 5173 port）** → 緩解：`localhost` 本身是 secure context 例外，但透過區網 IP 從手機連到開發機的 dev server 不算 secure context，`getUserMedia` 會被瀏覽器直接拒絕；人工驗證時需改用瀏覽器 remote debugging（如 Chrome DevTools 透過 USB port-forward，其 tunnel 視為 secure）、自簽憑證，或部署到已有 HTTPS 的環境後再測，列入 tasks.md 的驗證步驟前置說明，不在本次範疇內建置正式 HTTPS 開發環境
- **[風險] `signature` 驗證邏輯只在後端新增一段程式碼，但沒有現成 API 型別描述這個 request body（`api.generated.ts` 落後）** → 緩解：比照既有慣例在 `web/src/api/admin.ts` 手動撰寫 `redeemTicket(ticketId: string, signature: string | null)` 的型別簽章，不依賴自動產生
- **[風險] `jsQR` 在連續掃描迴圈中對同一張票或殘影重複偵測到相同內容，觸發多次核銷呼叫** → 緩解：決策 3 的結果橫幅顯示期間暫停偵測迴圈；決策 7 的當輪 dedupe 比對涵蓋殘影情境
- **[風險] `getUserMedia()` 的非同步 Promise 在使用者已切背景或元件已 unmount 之後才 resolve，若沒有保護會建立一份「已經沒人要的」stream／偵測迴圈** → 緩解：決策 4 的 generation 計數器保護，過期的初始化結果一律被丟棄並立即停止 track，不掛載也不啟動迴圈
- **[風險] 手機裝置逐幀（約 60 次/秒）執行完整 QR 解碼造成 CPU/電力/發熱負擔，長時間現場使用體驗變差** → 緩解：決策 1 的解碼頻率節流（約 10–15 次/秒）、`canvas`/`ImageData` buffer 重複使用、`processing`/`result` 期間不解碼
- **[風險] 背景/前景切換的狀態恢復邏輯分支較多（決策 4），實作與測試皆有一定複雜度** → 緩解：這是相機類頁面無法迴避的既有複雜度（不管有沒有明確設計都要處理），把分支明確寫進 design.md 並在 tasks.md 逐一列出對應測試，好過含糊帶過導致實作時各自為政

## 安全確認（CLAUDE.md 觸發條件：接受外部輸入）

- **前端 XSS**：掃描內容、手動輸入的 Ticket ID、核銷結果訊息全部以 Vue 樣板的文字插值（`{{ }}`）或 `:text` 類 binding 呈現，全程不使用 `v-html`，沒有任何未消毒使用者輸入被當 HTML 渲染的路徑；掃描到的內容本身也不會被拿去組出任何 URL 或 DOM 屬性字串。
- **API 呼叫 Auth Header**：`redeemTicket` 透過既有 `authorizedRequest`（`web/src/api/httpClient.ts`）呼叫，與 `web/src/api/admin.ts` 其餘既有函式共用同一套 Bearer token 注入與 401 換發重試邏輯，不另外處理。
- **輸入驗證**：外部輸入（QR 掃描內容、手動輸入 Ticket ID）在前端與後端各自驗證，前端僅為避免無效呼叫（決策 5），後端 `ITicketSigningService.TryVerify` 與既有的 Ticket 存在性/狀態檢查才是最終邊界（決策 2）。
- **SQL/Shell 注入**：本次變更全程透過既有 EF Core Repository（`ITicketRepository`）與參數化查詢，未新增任何原生 SQL 或 shell 指令組裝。

## Migration Plan

無資料庫 schema 異動（`signature` 驗證是無狀態的密碼學運算，`ErrorType` 新增列舉值不需要遷移）。後端變更為 `RedeemTicketHandler` 新增可選欄位與一段驗證分支，對既有未升級呼叫端（不帶 `signature`）行為完全不變，向下相容；隨既有 `api`／`web` 容器一般部署流程上線即可。回滾方式為還原本次變更的前後端 commit，無需資料回填或降版 migration。
