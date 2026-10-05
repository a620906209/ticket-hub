import http from 'k6/http';
import { API_BASE_URL } from './config.js';

const ERROR_BODY_LIMIT = 300;

export function authHeaders(token) {
  return { headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } };
}

export function withTags(params, name) {
  return Object.assign({}, params, { tags: { name } });
}

export function postJson(path, body, params) {
  return http.post(`${API_BASE_URL}${path}`, JSON.stringify(body), params);
}

export function get(path, params) {
  return http.get(`${API_BASE_URL}${path}`, params);
}

export function parseJson(response) {
  try {
    return response.json();
  } catch (_) {
    return undefined;
  }
}

/**
 * 錯誤摘要只含方法、路徑、狀態碼與截斷的回應 body；不含 request header（Authorization）或 token（design「安全確認」）。
 */
export function describeFailure(method, path, response) {
  const body = typeof response.body === 'string' ? response.body.slice(0, ERROR_BODY_LIMIT) : '';
  const networkError = response.error ? ` error=${response.error}` : '';
  return `${method} ${path} -> ${response.status}${networkError} ${body}`;
}
