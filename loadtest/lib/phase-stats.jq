# 分段耗時統計（export-measure-phases.sh 使用；tests/phase-stats.test.sh 以假資料自我測試）。
# 輸入：Seq /api/data 的 {Columns, Rows}，欄位為 Outcome 與各分段毫秒值（未走到的分段為 null）。
# 不用 Seq SQL 的 percentile()：實測它是近似值（同批 50 筆 EventLockWaitMs p95 精確值 0.731、Seq 回 0.779，
# 且不同分段被歸到同一個值），報告要引用的毫秒數必須是精確值，所以取原始值在這裡計算。
# 百分位以排序後線性內插（rank = p/100 × (n−1)），與 k6 Trend 的 p(N) 相同定義。
def percentile($p):
  sort as $sorted
  | ($sorted | length) as $n
  | if $n == 0 then null
    else ($p / 100 * ($n - 1)) as $rank
      | ($rank | floor) as $low
      | ($rank | ceil) as $high
      | $sorted[$low] + ($sorted[$high] - $sorted[$low]) * ($rank - $low)
    end;

def phase_stats($rows; $phases):
  { samples: ($rows | length) }
  + ([ $phases[] as $phase
       | ([ $rows[][$phase] | select(. != null) ]) as $values
       | { "\($phase)_n": ($values | length),
           "\($phase)_p50": ($values | percentile(50)),
           "\($phase)_p95": ($values | percentile(95)),
           "\($phase)_max": ($values | max) } ] | add);

. as $result
| [ $result.Columns[] | select(. != "Outcome") ] as $phases
| [ $result.Rows[] | [ $result.Columns, . ] | transpose | map({ (.[0]): .[1] }) | add ] as $rows
| { overall: phase_stats($rows; $phases),
    byOutcome: [ $rows | group_by(.Outcome)[] | { Outcome: .[0].Outcome } + phase_stats(.; $phases) ] }
