// 壓測共用常數與執行設定（k6-load-test design.md 決策 1、5、6、9、10）。

// 寫死，不讀 __ENV：k6 只能打 compose 內部的 api，不提供把壓測導向其他目標的入口（design「安全確認」）。
export const API_BASE_URL = 'http://api:8080';

export const OUTPUT_DIR = '/output';
export const TOKEN_FILE_PATH = `${OUTPUT_DIR}/tokens.json`;

export const BUYER_COUNT = 500;
export const TICKET_LIMIT = 50;

export const HOT_ZONE = 'HOT';
export const COUNT_TICKET_ZONE = 'GA';
export const TOTAL_SEATS = 2000;
export const OTHER_ZONE_COUNT = 19;
export const TICKET_PRICE = 1000;

export const START_DELAY_MS = 10_000;
export const PLACE_ORDER_MAX_DURATION = '70s';
// 70s 搶票 + 10s 票種快取 TTL + 5s 緩衝（design 決策 6）。
export const VERIFY_START_TIME = '85s';
export const SETUP_TIMEOUT = '180s';

export const API_BUILDS = ['debug', 'release'];
export const RUN_NUMBERS = ['1', '2', '3'];

// 各 scenario 可用的故障注入值；不適用的值視為設定錯誤而中止，避免「以為注入了故障、實際沒作用」。
export const FAULTS_BY_SCENARIO = {
  'count-ticket': ['p95', 'quantity51', 'bad-admin-token', 'bad-buyer-token', 'setup-timeout'],
  'seat-ticket': ['p95', 'bad-admin-token', 'bad-buyer-token', 'setup-timeout'],
};

// 小寫英數與連字號：標籤直接成為 /output 下的檔名前綴，不能含 `/` 或 `..`（design 決策 7）。
const MEASURE_TAG_PATTERN = /^[a-z0-9-]{1,32}$/;

/** 解析 `LT_MEASURE_TAG`；未帶（或空字串）時 `tag` 為 null，`isRequired` 時記為錯誤（無競爭基準必須帶標籤）。 */
export function parseMeasureTag(env, { isRequired }) {
  const raw = env.LT_MEASURE_TAG === undefined || env.LT_MEASURE_TAG === '' ? null : String(env.LT_MEASURE_TAG);
  if (raw === null)
    return { tag: null, isInvalid: false, errors: isRequired ? ['LT_MEASURE_TAG is required (lowercase letters, digits, hyphen; 1-32 chars)'] : [] };
  if (!MEASURE_TAG_PATTERN.test(raw))
    return { tag: null, isInvalid: true, errors: ['LT_MEASURE_TAG must match ^[a-z0-9-]{1,32}$'] };
  return { tag: raw, isInvalid: false, errors: [] };
}

/** 有標籤時加上 `measure-<tag>-` 前綴；標籤不合法時用固定的 invalid 前綴，不把原始輸入放進路徑。 */
export function applyMeasurePrefix(fileName, measureTag) {
  if (measureTag.isInvalid) return `measure-invalid-${fileName}`;
  return measureTag.tag === null ? fileName : `measure-${measureTag.tag}-${fileName}`;
}

/**
 * 解析 `LT_FAULT`／`LT_API_BUILD`／`LT_RUN`／`LT_MEASURE_TAG`。不拋例外：錯誤收集在 `errors`，由 setup 決定 `fail()`，
 * `handleSummary` 也能用同一份結果決定檔名。
 */
export function parseRunSettings(scenario, env) {
  const errors = [];
  const fault = env.LT_FAULT === undefined || env.LT_FAULT === '' ? null : String(env.LT_FAULT);
  if (fault !== null && !FAULTS_BY_SCENARIO[scenario].includes(fault))
    errors.push(`LT_FAULT must be one of ${FAULTS_BY_SCENARIO[scenario].join(', ')} for ${scenario}`);

  const isBuildValid = API_BUILDS.includes(env.LT_API_BUILD);
  if (!isBuildValid) errors.push(`LT_API_BUILD must be one of ${API_BUILDS.join(', ')}`);
  const build = isBuildValid ? env.LT_API_BUILD : 'invalid';

  const isRunValid = RUN_NUMBERS.includes(env.LT_RUN);
  if (fault === null && !isRunValid) errors.push(`LT_RUN must be one of ${RUN_NUMBERS.join(', ')}`);
  const run = isRunValid ? env.LT_RUN : null;

  const measureTag = parseMeasureTag(env, { isRequired: false });
  errors.push(...measureTag.errors);

  const baseFileName = fault !== null
    ? `${scenario}-fault-${fault}-summary.json`
    : `${scenario}-${build}-${run === null ? 'run-invalid' : `run${run}`}-summary.json`;

  return {
    scenario,
    fault,
    build,
    run,
    measureTag: measureTag.tag,
    summaryPath: `${OUTPUT_DIR}/${applyMeasurePrefix(baseFileName, measureTag)}`,
    // 正式執行與量測執行（非故障注入、設定合法）的檔名需要防覆寫；故障注入與 invalid 檔名不在彙整白名單內。
    isSummaryOverwriteProtected: fault === null && errors.length === 0,
    errors,
  };
}

export function hasFault(settings, fault) {
  return settings.fault === fault;
}

/**
 * 把 JWT 簽章段換成同長度的 'A'，保證簽章驗證失敗（只改最後一個字元可能只動到 base64 padding bits，簽章不變）。
 */
export function tamperToken(token) {
  const lastDot = token.lastIndexOf('.');
  const signature = token.slice(lastDot + 1);
  return `${token.slice(0, lastDot + 1)}${'A'.repeat(signature.length)}`;
}
