// 顯示層的金額格式化，不參與任何金額計算或驗證（見設計文件決策 7）；不處理負數／NaN／Infinity，
// 上游（表單驗證、後端 Validator）已經擋掉這些不合法的值。
export function formatCurrency(amount: number): string {
  return `NT$${amount.toLocaleString('en-US', { maximumFractionDigits: 2 })}`
}
