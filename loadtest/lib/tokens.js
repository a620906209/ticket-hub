import { SharedArray } from 'k6/data';
import { TOKEN_FILE_PATH } from './config.js';

// SharedArray 的 callback 只執行一次，500 個 VU 共用同一份唯讀資料，不必各自解析整個 token 檔。
// 檔案不存在時 open() 在 init 階段拋例外，k6 直接中止、不會送出任何請求。
export const buyerTokens = new SharedArray('buyer-tokens', () => {
  const tokens = JSON.parse(open(TOKEN_FILE_PATH)).buyerTokens;
  return Array.isArray(tokens) ? tokens : [];
});

export const adminTokens = new SharedArray('admin-token', () => {
  const token = JSON.parse(open(TOKEN_FILE_PATH)).adminToken;
  return typeof token === 'string' ? [token] : [];
});

/** 回傳錯誤訊息（不含 token 內容）；沒問題時回傳 null。 */
export function validateTokens(minimumBuyerCount) {
  if (adminTokens.length !== 1 || adminTokens[0] === '') return 'token file has no admin token';
  if (buyerTokens.length < minimumBuyerCount)
    return `token file has ${buyerTokens.length} buyer token(s), need at least ${minimumBuyerCount}; rerun the seeder`;
  const distinct = new Set();
  for (let i = 0; i < buyerTokens.length; i++) {
    const token = buyerTokens[i];
    if (typeof token !== 'string' || token === '') return `buyer token #${i} is empty`;
    distinct.add(token);
  }
  if (distinct.size !== buyerTokens.length) return 'token file has duplicate buyer tokens';
  return null;
}
