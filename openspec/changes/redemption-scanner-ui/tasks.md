## 0. 規格對齊與 QR 驗章測試（`ProjectC.Infrastructure.Tests`）

- [x] 0.1 確認既有 `HmacTicketSigningService.TryVerify` 單元測試已涵蓋合法內容驗證成功並還原 Ticket ID
  - 對應 AC: TICKET-ISSUANCE-QR-VERIFY-VALID（既有測試 `TryVerify_WhenContentSignedByThisService_ReturnsTrueAndRestoresTicketId` 已涵蓋，僅需在 PR 說明中註記確認）
- [x] 0.2 確認既有 `HmacTicketSigningService.TryVerify` 單元測試已涵蓋竄改後驗章失敗
  - 對應 AC: TICKET-ISSUANCE-QR-VERIFY-TAMPERED（既有測試 `TryVerify_WhenContentTamperedByOneCharacter_ReturnsFalse` 已涵蓋，僅需在 PR 說明中註記確認）
- [x] 0.3 補齊 `HmacTicketSigningService.TryVerify` 格式不符輸入的單元測試：`null`、空字串、僅空白字元、缺少分隔符、多個 `.` 分隔符、前段非合法 `"D"` 格式 GUID、空簽章後段，皆安全回傳 `false` 且不拋例外
  - 對應 AC: TICKET-ISSUANCE-QR-VERIFY-MALFORMED（既有測試僅涵蓋 `null`／空字串／單字元竄改，其餘情境需新增）

## 1. 後端：核銷 API 新增可選簽章驗證

- [x] 1.1 `ErrorType`（`ProjectC.Application.Common`）新增 `InvalidTicketSignature`；`Error` 新增對應工廠方法 `Error.InvalidTicketSignature(string message)`（比照既有 `ErrorType.QueueAdmissionRequired` 的新增方式）
- [x] 1.2 `ResultExtensions.CreateProblemResult` 的 `statusCode` switch 新增 `ErrorType.InvalidTicketSignature => StatusCodes.Status400BadRequest`（`Title` 沿用既有邏輯 `error.Type.ToString()`）
- [x] 1.3 `RedeemTicketRequest`（Application 層，`ProjectC.Application.Tickets.RedeemTicket`）新增 record：`string? Signature`
- [x] 1.4 `RedeemTicketHandler.HandleAsync` 新增參數接受 `string? signature`，注入 `ITicketSigningService`；`signature` 非 `null` 時一律重組 `{id:D}.{signature}` 呼叫 `TryVerify`（不對空字串/空白做特殊分支，讓 `TryVerify` 自然判定失敗），失敗回傳 `Error.InvalidTicketSignature(...)`（不查詢/不鎖定 Ticket）；`signature` 為 `null` 時行為與現況完全相同（design.md 決策 2）；不得在任何 log 輸出 `signature` 值
- [x] 1.5 `AdminTicketsController.Redeem` 改為接受 `[FromBody] RedeemTicketRequest? request`（body 可省略，省略時視為 `signature = null`，向下相容既有無 body 呼叫）
- [x] 1.6 單元測試（`ProjectC.Application.Tests`，Moq）：
  - 對應 AC: TICKET-REDEEM-SIG-BACKWARD-COMPAT（未提供簽章，成功／404／409 三種既有案例行為不變）
  - 對應 AC: TICKET-REDEEM-SIG-VALID（正確簽章成功核銷）
  - 對應 AC: TICKET-REDEEM-SIG-INVALID（竄改簽章回傳 `InvalidTicketSignature`，未呼叫 `GetForUpdateAsync`）
  - 對應 AC: TICKET-REDEEM-SIG-EMPTY（空字串／空白字元簽章回傳 `InvalidTicketSignature`，未呼叫 `GetForUpdateAsync`）
- [x] 1.7 整合測試（`ProjectC.WebApi.Tests`，Testcontainers）：
  - 對應 AC: TICKET-REDEEM-SIG-BACKWARD-COMPAT／TICKET-REDEEM-SIG-VALID／TICKET-REDEEM-SIG-INVALID（真實 API 呼叫的 HTTP 狀態碼與 `ProblemDetails.Title`，驗證 `Title` 確實為 `"InvalidTicketSignature"`，可與其他 400 區分）
  - 對應 AC: TICKET-REDEEM-SIG-TYPE-MISMATCH（`signature` 帶數字型別，驗證回傳 400 且未變更 Ticket 狀態）

## 2. 前端 API 呼叫前置調整

- [x] 2.1 `web/src/api/httpClient.ts` 的 `RequestOptions.method` 聯集型別新增 `'PATCH'`（design.md Context）
- [x] 2.2 `web/src/api/admin.ts` 新增 `redeemTicket(ticketId: string, signature: string | null): Promise<void>`，呼叫 `PATCH /admin/tickets/${ticketId}/redeem`，body 為 `{ signature }`（204 No Content，比照既有 `authorizedRequest` 用法）

## 3. 前端核銷核心邏輯（可獨立於相機測試）

- [x] 3.1 撰寫 `parseTicketIdFromQrContent(content: string)`：依 `ticket-issuance` 能力定義的精確格式驗證恰好一個 `.` 分隔符、前段為合法 `D` 格式 GUID（不分大小寫皆接受，內部正規化）、後段（簽章）非空字串；三者皆符合才回傳 `{ ticketId, signature }`，否則回傳「無法辨識」結果，不呼叫 API（design.md 決策 5）——備註：此為避免無效呼叫的前端檢查，非安全邊界
- [x] 3.2 撰寫 `parseTicketIdFromManualInput(value: string)`：trim 後只接受單一合法 GUID，不接受 `.` 或附加內容，不合法時回傳「Ticket ID 格式不正確」，不呼叫 API（design.md 決策 5）
- [x] 3.3 撰寫核銷呼叫的整合邏輯：`parseTicketIdFromQrContent`／`parseTicketIdFromManualInput` 的解析結果原封不動傳入 `redeemTicket(ticketId, signature)`（掃描路徑傳解析出的 `signature`，手動輸入路徑固定傳 `null`），並依回應對應結果：204 成功／409 已核銷過／404 查無此票／`error.status === 400 && error.problem?.title === 'InvalidTicketSignature'` 才判定為簽章無效（比照既有 `QueueAdmissionRequired` 的 `title` 判別寫法，`web/src/pages/buyer/EventDetailPage.vue:370`，不得只憑 400 狀態碼判斷）／其餘（含其他 400、5xx、網路例外）視為系統錯誤，不得歸類為查無此票或簽章無效，不自動重試（design.md 決策 4）
- [x] 3.4 單元測試：`parseTicketIdFromQrContent` 對合法格式、缺分隔符、多個 `.` 分隔符、非合法 GUID 前段、空簽章後段的輸入分別驗證結果（多個分隔符對應 design.md 決策 5「恰好一個 `.` 分隔符」要求，須回傳無法辨識而非誤判為可解析）——對應 AC: ADMIN-REDEEM-SCAN-UNRECOGNIZED
- [x] 3.5 單元測試：`parseTicketIdFromManualInput` 對合法 GUID、非法格式（含帶 `.` 的內容）分別驗證結果——對應 AC: ADMIN-REDEEM-MANUAL-INVALID-FORMAT
- [x] 3.6 單元測試（結果對應邏輯，mock `redeemTicket` 回應）：204／409／404／`InvalidTicketSignature` 400／其他 400／5xx／網路例外下分別產生正確的結果狀態，且「其他 400」與「5xx/網路例外」皆不得產生「查無此票」或「簽章無效」的結果——對應 AC: ADMIN-REDEEM-SCAN-SUCCESS, ADMIN-REDEEM-SCAN-CONFLICT, ADMIN-REDEEM-SCAN-NOT-FOUND, ADMIN-REDEEM-SCAN-INVALID-SIGNATURE, ADMIN-REDEEM-SCAN-SYSTEM-ERROR
- [x] 3.7 單元測試（端到端串接，mock `redeemTicket` 只驗證呼叫參數不驗證回應）：給定掃描字串 `{ticketId}.{signature}`，驗證 `redeemTicket` 被呼叫時的參數恰好是解析出的 `ticketId`／`signature`，未被中途轉換或遺漏——對應 AC: ADMIN-REDEEM-SCAN-DISPATCH（回應審查意見：先前只各自測試 parser 與「給定回應碼產生什麼結果」，沒有測試中間這段呼叫參數是否正確傳遞）

## 4. 核銷掃碼頁面元件

- [x] 4.1 安裝 npm 依賴 `jsqr`（design.md 決策 1，Apache-2.0 授權）
- [x] 4.2 建立 `web/src/pages/admin/RedemptionScannerPage.vue`：實作 design.md 決策 4 的完整狀態機（`initializing`／`scanning`／`processing`／`result`／`camera-unavailable`／`permission-denied`／`unsupported`／`error`），能力偵測（`window.isSecureContext`、`navigator.mediaDevices?.getUserMedia`）失敗時直接進 `unsupported`，不嘗試呼叫 `getUserMedia`
- [x] 4.3 相機串流：`getUserMedia({ video: { facingMode: 'environment' } })` 餵給 `<video autoplay muted playsinline>`，`<canvas>` 與其 `ImageData` buffer 只在初始化時建立一次並重複使用；`requestAnimationFrame` 迴圈驅動，但實際呼叫 `jsQR` 解碼的頻率節流在約 10–15 次/秒（時間戳記比對，未達間隔的 frame 略過，不 draw canvas 也不解碼），且 `video.readyState`／`videoWidth` 未就緒或 `processing`／`result` 狀態下完全不解碼（design.md 決策 1）；掃描邏輯抽成可注入/可 mock 的介面，供第 3 節的核心邏輯單元測試不依賴真實相機 API
- [x] 4.4 例外分類：`getUserMedia` 的 `NotAllowedError` → `permission-denied`；`NotFoundError`／`OverconstrainedError`（含後鏡頭 constraint 不滿足）→ `camera-unavailable`；其他 → `error`（design.md 決策 1、決策 4）
- [x] 4.5 Generation 計數器保護：每次進入 `initializing` 遞增計數器並記住當下值；`hidden`／unmount 時計數器再遞增；`getUserMedia()` resolve/reject 時比對記住的值與目前最新值，不相等（已過期）則對取得的 stream 呼叫 `stop()` 後直接捨棄，不掛 `video.srcObject`、不啟動偵測迴圈、不更動狀態（design.md 決策 4「Race condition／過期非同步初始化的取消保護」）
- [x] 4.6 生命週期收尾（`hidden`）：元件 unmount 或 `document.visibilitychange` 為 `hidden` 時，停止偵測迴圈並對 stream 的每個 track 呼叫 `stop()`、清除 `video.srcObject`，並記錄切背景前的狀態；若切背景當下為 `processing`，讓呼叫自然完成、不中途取消；任何時間點只允許一份 stream／一個偵測迴圈存在（design.md 決策 4）
- [x] 4.7 生命週期恢復（`visible`）：元件已 unmount 時 MUST NOT 重新初始化（監聽器需在 unmount 一併移除）；切背景前為 `processing` 者等待該次呼叫完成後依結果進入 `result`（不重新送出核銷）；切背景前為 `result` 者先恢復顯示原結果，結果顯示完才重新初始化相機；切背景前為 `scanning` 者重新執行一次 `initializing`；切背景前為 `unsupported`／`permission-denied`／`camera-unavailable`／`error` 者 MUST NOT 自動重試，維持手動輸入表單（`unsupported` 不顯示「重新嘗試相機」按鈕，其餘三者顯示）——對應 design.md 決策 4 的 `hidden`/`visible` 完整規則
- [x] 4.8 單元測試：`visibilitychange` 為 `hidden` 時停止偵測迴圈與 stream track；恢復 `visible` 時依切背景前狀態分別驗證（`processing` 不重送、`result` 先顯示結果才重啟相機、`scanning` 自動重新初始化、`permission-denied`/`camera-unavailable`/`error` 不自動重試且顯示重試按鈕、`unsupported` 不自動重試且不顯示按鈕）；元件已 unmount 後收到 `visibilitychange` 不會重新初始化——對應 design.md 決策 4 的生命週期規則；`processing` 不重送分支對應 AC: ADMIN-REDEEM-BACKGROUND-PROCESSING-COMPLETES
- [x] 4.9 單元測試（generation 保護）：`getUserMedia()` 尚未 resolve 前觸發 `hidden`，待其稍後 resolve 時驗證取得的 stream 立即被 `stop()`、未掛上 `video.srcObject`、未啟動偵測迴圈；`getUserMedia()` 尚未 resolve 前元件 unmount，待其稍後 resolve 時驗證不更新任何狀態——對應 design.md 決策 4 的 race condition 保護
- [x] 4.10 結果橫幅：依成功/已核銷過/查無此票/簽章無效/無法辨識/系統錯誤六種狀態套用不同顏色、文字與圖示（不僅用顏色區分）；成功與已核銷過用 `role="status"` `aria-live="polite"`，其餘四種用 `role="alert"` `aria-live="assertive"` 並將焦點移至結果橫幅；停留秒數抽成常數（成功預設 1.5 秒自動恢復，其餘預設 4 秒並提供「立即繼續掃描」按鈕，秒數本身為 design 層級的可調整預設值，非 spec 契約）（design.md 決策 3）
- [x] 4.11 結果顯示期間暫停偵測，並比對「與上一次已送出核銷的內容是否相同」，相同則忽略；此比對記憶僅限當輪 `processing`／`result` 期間，一旦恢復 `scanning`（自動或手動）立即清除，不做跨輪次持久化比對（design.md 決策 7）——對應 AC: ADMIN-REDEEM-SCAN-DEDUPE, ADMIN-REDEEM-SCAN-RETRY-AFTER-ERROR
- [x] 4.11a 單元測試：`result` 顯示期間模擬掃描介面持續回報相同內容，驗證 `redeemTicket` 只被呼叫一次（不因殘留偵測而重複呼叫）——對應 AC: ADMIN-REDEEM-SCAN-DEDUPE（先前 4.11 只有實作任務）
- [x] 4.11b 單元測試：模擬系統錯誤結果後恢復 `scanning`，再次掃到與前次完全相同的內容，驗證 `redeemTicket` 被重新呼叫（不因「內容與上次相同」被永久忽略）——對應 AC: ADMIN-REDEEM-SCAN-RETRY-AFTER-ERROR（先前 4.11 只有實作任務）
- [x] 4.12 `scanning` 狀態下畫面提供常駐「改用手動輸入」按鈕，點選後直接切到手動輸入表單，不需等待相機判定失敗（design.md 決策 6）——對應 AC: ADMIN-REDEEM-MANUAL-SWITCH
- [x] 4.13 `unsupported` 狀態顯示手動輸入表單、不顯示「重新嘗試相機」按鈕，並顯示「此瀏覽器不支援相機掃描」等對應說明文字——對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED
- [x] 4.14 `camera-unavailable`／`permission-denied`／`error` 三個狀態顯示手動輸入表單、顯示「重新嘗試相機」按鈕，並各自顯示不同的說明文字（例如「找不到可用相機」／「相機權限被拒絕」／「相機初始化發生錯誤」，不得共用同一句籠統訊息）——對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE
- [x] 4.13a 單元測試：`initializing` 直接以 `unsupported`／`permission-denied`／`camera-unavailable`／`error` 四種初始狀態掛載元件，分別驗證手動輸入表單為畫面主體、對應的說明文字、以及「重新嘗試相機」按鈕的有無——對應 AC: ADMIN-REDEEM-MANUAL-FALLBACK-UNSUPPORTED, ADMIN-REDEEM-MANUAL-FALLBACK-RETRIABLE（先前 4.13/4.14 只有實作任務，4.8 的測試只涵蓋 visibilitychange 情境下的重試按鈕顯示，缺初始掛載情境的獨立測試）
- [x] 4.13b 單元測試：點擊「重新嘗試相機」後模擬初始化再次失敗（原因可與前次不同），驗證畫面維持手動輸入表單、說明文字依新的失敗原因更新、不卡在載入中畫面——對應 AC: ADMIN-REDEEM-MANUAL-RETRY-CAMERA-STILL-FAILS
- [x] 4.15 介面標示：掃描模式旁標註「已驗證簽章」、手動輸入模式旁標註「Admin 信任操作，未驗證簽章」等文字說明兩者性質差異——對應 AC: ADMIN-REDEEM-TRUST-LABEL
- [x] 4.16 單元測試：手動輸入核銷成功（`signature: null`）、已核銷過、查無此票、系統錯誤四種情境——對應 AC: ADMIN-REDEEM-MANUAL-SUCCESS, ADMIN-REDEEM-MANUAL-CONFLICT, ADMIN-REDEEM-MANUAL-NOT-FOUND, ADMIN-REDEEM-MANUAL-SYSTEM-ERROR
- [x] 4.17 單元測試：核銷完成後依結果類型於對應秒數自動恢復可掃描狀態；錯誤類結果的「立即繼續掃描」按鈕可提前恢復——對應 AC: ADMIN-REDEEM-SCAN-AUTO-RESUME
- [x] 4.18 單元測試（decode 節流，若解碼節流抽成獨立可測的純函式）：時間戳記間隔小於節流門檻時略過解碼、達到門檻才解碼一次——對應 design.md 決策 1
- [x] 4.19 單元測試：`scanning` 狀態下點擊「改用手動輸入」按鈕，畫面切換為手動輸入表單——對應 AC: ADMIN-REDEEM-MANUAL-SWITCH（先前只有 4.12 的實作任務，缺對應測試）
- [x] 4.20 單元測試：掃描模式與手動輸入模式分別渲染出對應的信任標示文字（「已驗證簽章」／「Admin 信任操作，未驗證簽章」）——對應 AC: ADMIN-REDEEM-TRUST-LABEL（先前只有 4.15 的實作任務，缺對應測試）
- [x] 4.21 單元測試（背景/前景切換的使用者可觀察行為）：`scanning` 狀態下觸發 `hidden` 再 `visible`，相機於前景恢復後仍可正常掃描（不卡在無 stream 的假 `scanning` 畫面、不重複觸發核銷）——對應 AC: ADMIN-REDEEM-BACKGROUND-RESUME（第 4.8/4.9 已涵蓋內部狀態轉換細節，此處驗證的是使用者實際可觀察到的最終行為）

## 5. 路由與導覽

- [x] 5.1 `web/src/router/index.ts` 在既有 `/admin` 巢狀路由下新增 `{ path: 'redeem', name: 'admin-redeem', component: RedemptionScannerPage }`，沿用既有 Admin 角色守衛，不新增守衛邏輯
- [x] 5.2 `web/src/layouts/AdminLayout.vue` 的 `el-menu` 新增「票券核銷」項目（`index="/admin/redeem"`），比照既有場館/活動/訂單管理三個既有項目的寫法——對應 AC: ADMIN-REDEEM-NAV-ENTRY
- [x] 5.3 單元測試：路由測試補一個 `/admin/redeem` 案例，確認一般會員/未登入導向規則與既有 `/admin/*` 一致（沿用既有 `router/index.test.ts` 模式）
- [x] 5.4 單元測試：`AdminLayout.vue` 導覽選單渲染出「票券核銷」項目，且連結指向 `/admin/redeem`——對應 AC: ADMIN-REDEEM-NAV-ENTRY（先前只有 5.2 的實作任務，缺對應測試）

## 6. 驗證

- [x] 6.1 `docker compose exec api dotnet test` 全數通過（含第 0、1 節新增/確認的單元測試與整合測試）
- [x] 6.2 `docker compose exec web npm run lint`、`docker compose exec web npm run test`、`docker compose exec web npm run build`（`build` 內含 `vue-tsc` 型別檢查，僅跑 lint/test 無法涵蓋型別錯誤）全數通過
- [x] 6.3 啟動 dev server，用瀏覽器（含 claude-in-chrome）手動驗證手動輸入路徑：核銷成功、核銷已核銷過的票券、核銷不存在的 Ticket ID、格式不正確四種結果訊息正確顯示；確認裝置/瀏覽器自動化環境下（無相機或非 secure context）頁面正確落到手動輸入模式；確認掃描畫面下「改用手動輸入」按鈕可正常切換
- [x] 6.4 相機掃描路徑因瀏覽器自動化環境通常無真實相機、且透過區網 IP 存取非 secure context，改以實機（手機/平板，特別是 iOS Safari）搭配可信任的 HTTPS 來源（例如 Chrome DevTools USB port-forward 或已部署的 HTTPS 環境）人工驗證一次完整掃描流程，包含掃到正確簽章、掃到竄改內容、結果橫幅與依結果類型的停留/自動恢復、切到背景再切回前景後相機能正確恢復是否如預期（實機：iPhone Safari，透過 mkcert 區網 HTTPS 憑證存取；過程中發現並修復黑屏 bug，見 `useRedemptionScanner.ts` 的 videoElement watch，strict-reviewer PASS）
