// xUnit 預設會用機器核心數當平行執行緒上限，讓所有測試 collection（每個未標註 [Collection] 的類別
// 都是自己的隱含 collection）同時跑；這個組合裡有大量各自啟動 Testcontainers（Postgres／Redis）的
// 測試類別，容器數量隨著測試量增加而線性成長，會讓 Docker 負載與 HTTP 請求排程延遲一起升高，
// 使原本就對時序／共用限流狀態敏感的既有測試（例如 PurchaseQueueAdmissionServiceLeaderElectionTests
// 的分散式鎖互斥計時、BackgroundServiceTraceIdTests 共用的緊縮 LoginRateLimiting 視窗）更容易在
// 高負載下 flaky（query-caching 新增大量測試後才觀察到，見 strict-reviewer 審查發現）。
// 限制平行執行緒數降低同時啟動的容器/請求量，換取測試套件執行時間變長。
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
