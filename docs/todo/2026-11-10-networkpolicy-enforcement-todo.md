# NetworkPolicy 在本機無法驗證（2026-11-10）

## 背景

`docs/todo/2026-11-02-deployment-prerequisites.md` 第 1 項要求：只讓可信來源存取 `/health/*`，並實測「探針來源可達、維運來源可達、不可信 workload 與外部來源不可達」。`k8s-local-manifests` 的 `k8s/60-networkpolicy.yaml` 已宣告規則（api 只收 `app=web`；db 只收 `app=api` 與 `app=migrator`；redis、seq 只收 `app=api`）。

## 現況

- Docker Desktop kubeadm 模式的 `kube-system` 只有 coredns、etcd、apiserver、controller-manager、kube-proxy、scheduler、storage-provisioner、vpnkit-controller，沒有 Calico／Cilium 等會執行 NetworkPolicy 的元件。
- 對照實驗（`k8s-local-manifests` task 1.1(c)，每次執行 `scripts/verify-k8s-manifests.sh` 也會重做）：套用 deny-all ingress NetworkPolicy 的 Pod 仍可被另一個 Pod 連通 → 本機 **不執行** NetworkPolicy。
- 因此「不可信 workload 不可達」在本機未驗證，腳本斷言 8c 輸出 `UNVERIFIED`（結束碼 3），不算 PASS。已驗證的部分（api 無對外 Service、經 web 取不到 `/health/*`、`port-forward` 可達、探針使 Pod Ready）不受影響。
- 上線 gate 因此**未解除**：在可執行 NetworkPolicy 的環境驗證前，不得讓 api 暴露給不受信任的網路或工作負載。

## 需要的環境

可執行 NetworkPolicy 的 CNI，例如 kind／k3d 搭配 Calico 或 Cilium，或任何已啟用 NetworkPolicy 的雲端叢集。不要在 Docker Desktop 的叢集上改裝 CNI（風險高、超出範圍）。

## 驗證步驟

1. 在該環境以 `k8s/` 部署（同一份 manifest）。
2. 跑 `bash scripts/verify-k8s-manifests.sh`（腳本目前只允許 context `docker-desktop`；改用其他叢集時須放寬 `CONTEXT` 守衛，並確認 NodePort 可達方式）。若對照實驗顯示 CNI 有執行，斷言 8c 會自動改為 PASS／FAIL：不可信 Pod 連不到 `api:8080`、`redis:6379`、`db:5432`，`app=web` 的 Pod 連得到 `api:8080`。
3. 確認 kubelet 探針仍使 Pod Ready（NetworkPolicy 不應擋節點到 Pod 的探針；若 CNI 會擋，須放行節點來源）。
4. 注意：斷言 8c 的「CNI 有執行」分支在本機從未實際執行過（只做過語法檢查），第一次在能執行 NetworkPolicy 的環境跑時，請當作新程式碼檢查其輸出。

## 解除條件

上述驗證在可執行 NetworkPolicy 的環境通過後：更新 `docs/todo/2026-11-02-deployment-prerequisites.md` 第 1 項，並刪除本檔或移到 `docs/archive/`。
