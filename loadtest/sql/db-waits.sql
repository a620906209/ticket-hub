-- 資料庫等待事件取樣（order-placement-p95-optimization design.md 決策 2），由 sample-db-waits.sh 以 \watch 重複執行。
-- 結尾刻意不加分號：腳本在後面接 \watch，讓這段查詢只在 \watch 時執行，避免多跑一次未計數的取樣。
-- LEFT JOIN：資料庫沒有其他連線時也輸出一列（connections = 0），腳本才能以「取樣時間點數 = 預期次數」確認取樣沒有中斷。
-- 排除取樣連線自己（pg_backend_pid）與取樣工具的管理連線（application_name lt-db-waits*）。
SELECT
  to_char(sample.sampled_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"') AS sampled_at_utc,
  coalesce(activity.state, '') AS state,
  coalesce(activity.wait_event_type, '') AS wait_event_type,
  coalesce(activity.wait_event, '') AS wait_event,
  count(activity.pid) AS connections
FROM (SELECT clock_timestamp() AS sampled_at) AS sample
LEFT JOIN pg_stat_activity AS activity
  ON activity.datname = current_database()
  AND activity.pid <> pg_backend_pid()
  AND coalesce(activity.application_name, '') NOT LIKE 'lt-db-waits%'
GROUP BY 1, 2, 3, 4
ORDER BY 1, 2, 3, 4
