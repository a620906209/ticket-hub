# api 缺少 Jwt 設定時結束碼為 139（2026-11-10）

## 背景

`production-images` 驗證腳本的斷言 7：production 映像缺少必要的 Jwt 設定時，api 容器必須啟動失敗。預期 .NET 未處理例外會以 SIGABRT 結束（結束碼 134），實際為 **139（128 + 11，SIGSEGV）**。

## 現況

- 斷言 7 只要求「結束碼非零」且日誌含缺少 Jwt 設定的錯誤，所以通過；已在 `production-images` 的 tasks.md 記為偏差，使用者 2026-11-10 接受。
- 根因**未查明**。可能方向（皆未驗證）：.NET runtime 崩潰處理時 segfault；Alpine／非 root（`USER $APP_UID`）環境差異；是否在 Debian 基底的 aspnet 映像上復現。
- 對 Kubernetes 無實質影響：`CrashLoopBackOff` 只看是否非零結束。
- K8s 實測（`k8s-local-manifests` 斷言 7，2026-11-10，Docker Desktop）：缺 `Jwt__SigningKey` 的 api Pod 進入 `CrashLoopBackOff`，`lastState.terminated.exitCode` 為 **139**，與 compose 驗證一致；`kubectl logs` 顯示 `Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: DataAnnotation validation failed for 'JwtOptions' members: 'SigningKey' with the error: 'The SigningKey field is required.'`。未做根因分析。

## 待辦

- [ ] 在 `k8s-local-manifests` 做缺設定演練時，用 `kubectl logs` / `kubectl describe pod` 確認崩潰輸出與結束碼。
- [ ] 若要查根因：本機以 `docker run` 重現，比對 `DOTNET_` 崩潰相關設定、有無 `USER` 的差異。
- [ ] 查明或決定不處理後，刪除本檔或移到 `docs/archive/`。
