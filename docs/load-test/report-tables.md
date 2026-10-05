### Release × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 1182.84 | 1211.40 | 50 | 450 | 0 | 50 | 92.00 | 未通過 |
| 2 | 802.33 | 815.69 | 50 | 450 | 0 | 50 | 100.00 | 未通過 |
| 3 | 779.74 | 792.89 | 50 | 450 | 0 | 50 | 74.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 802.33／最小值 779.74／最大值 1182.84
- P99 (ms)（3 次有值）：中位數 815.69／最小值 792.89／最大值 1211.40
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Release × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 5403.99 | 5628.98 | 50 | 450 | 0 | 50 | 81.00 | 未通過 |
| 2 | 4834.73 | 5019.62 | 50 | 450 | 0 | 50 | 90.00 | 未通過 |
| 3 | 4818.04 | 5088.10 | 50 | 450 | 0 | 50 | 93.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 4834.73／最小值 4818.04／最大值 5403.99
- P99 (ms)（3 次有值）：中位數 5088.10／最小值 5019.62／最大值 5628.98
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 數量票（count-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 604.85 | 617.30 | 50 | 450 | 0 | 50 | 102.00 | 未通過 |
| 2 | 611.44 | 626.33 | 50 | 450 | 0 | 50 | 81.00 | 未通過 |
| 3 | 627.89 | 642.47 | 50 | 450 | 0 | 50 | 107.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 611.44／最小值 604.85／最大值 627.89
- P99 (ms)（3 次有值）：中位數 626.33／最小值 617.30／最大值 642.47
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500

### Debug × 座位票（seat-ticket）

| 次 | P95 (ms) | P99 (ms) | 成功數 | 409 數 | 5xx 數 | QuantitySold | 送出時距 max−min (ms) | 單次判定 |
|---|---|---|---|---|---|---|---|---|
| 1 | 4996.08 | 5221.53 | 50 | 450 | 0 | 50 | 113.00 | 未通過 |
| 2 | 5019.12 | 5199.94 | 50 | 450 | 0 | 50 | 80.00 | 未通過 |
| 3 | 4983.35 | 5161.49 | 50 | 450 | 0 | 50 | 118.00 | 未通過 |

- P95 (ms)（3 次有值）：中位數 4996.08／最小值 4983.35／最大值 5019.12
- P99 (ms)（3 次有值）：中位數 5199.94／最小值 5161.49／最大值 5221.53
- 判定：未通過
  - 第 1 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 2 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
  - 第 3 次：runVerdict 未通過：threshold failed: http_req_duration{name:place-order}: p(95)<500
