# ReconcileEventQueueAsync 認知複雜度待辦（2026-11-10）

## 背景

CI 的 SonarAnalyzer 對 `src/ProjectC.WebApi/BackgroundServices/PurchaseQueueAdmissionService.cs` 的 `ReconcileEventQueueAsync` 報 S3776（RSPEC-3776）：認知複雜度 28，上限 15。目前只是警告，不影響測試與建置。

同次警告中的 S2692（`JwtBearerProblemDetailsResponses`，刻意 `> 0`，已加說明性抑制）與 S3358（`FieldErrorKeyFormatter` 巢狀三元，已抽方法）已處理；本項因屬佇列校正核心邏輯而延後。

## 為何不順手改

- 這是 Redis 鏡像逐筆定向校正，邏輯歷經多輪審查（程式碼註解有「第十輪審查修正」，對應 design.md Decision 5 與 PQLE-REBUILD-005），順手拆分的風險高於收益。
- 拆分必須走正式流程：RED／GREEN 證據、hardener、strict-reviewer 與對抗式審查。

## 建議作法（尚未定案）

- 依 Decision 5 的步驟把 `ReconcileEventQueueAsync` 拆成數個私有方法，例如：補齊 waiting（步驟 3）、補齊 admitted（步驟 4）、清除殘留、逾時與放棄的推進決策修復，主方法只負責串接。
- 拆分前先確認既有測試對每個步驟都有覆蓋；不足的先補測試再重構。
- 行為須完全不變：所有步驟維持冪等，`TryReconcileOperationAsync` 的錯誤處理與 `CancellationToken` 傳遞不改。
- 撰寫此待辦時只讀了方法前段（到步驟 4），後半（清除殘留、逾時修復）尚未讀，拆法須在讀完全部後決定。

## 替代方案

若判斷不值得拆，可在該方法上加 `SuppressMessage`（S3776）並寫明理由，同時把理由記到 design.md。

## 解除條件

S3776 警告消失，或抑制與理由已記錄；相關測試全數通過。
