// [LT-MEASURE-008] [LT-MEASURE-009] LT_MEASURE_TAG 解析自我測試（order-placement-p95-optimization design.md 決策 7）。
// 量測輸出若誤用正式檔名，會被彙整腳本當成正式驗收結果；標籤若可含 `/`，可能寫出 /output 之外。
import { check } from 'k6';
import { parseMeasureTag, parseRunSettings } from '../lib/config.js';
import { whitelistFileNames } from '../lib/aggregate.js';

export const options = {
  vus: 1,
  iterations: 1,
  thresholds: { checks: ['rate==1'] },
};

const OFFICIAL_ENV = { LT_API_BUILD: 'release', LT_RUN: '1' };

function fileNameOf(path) {
  return path.slice(path.lastIndexOf('/') + 1);
}

export default function () {
  const whitelist = whitelistFileNames().map((entry) => entry.fileName);

  for (const scenario of ['count-ticket', 'seat-ticket']) {
    const official = parseRunSettings(scenario, OFFICIAL_ENV);
    const tagged = parseRunSettings(scenario, { ...OFFICIAL_ENV, LT_MEASURE_TAG: 'before' });
    const taggedFileName = fileNameOf(tagged.summaryPath);

    check(tagged, {
      [`${scenario}: tagged summary is measure-<tag>-<official name>`]: (s) =>
        taggedFileName === `measure-before-${fileNameOf(official.summaryPath)}` && s.summaryPath.startsWith('/output/'),
      [`${scenario}: tagged summary is not in aggregate whitelist`]: () => !whitelist.includes(taggedFileName),
      [`${scenario}: untagged official summary is still in whitelist`]: () => whitelist.includes(fileNameOf(official.summaryPath)),
      [`${scenario}: valid tag has no errors`]: (s) => s.errors.length === 0 && s.measureTag === 'before',
      // 量測輸出同樣不得覆寫（design 決策 3「檔案已存在時中止，與 k6 一致」）。
      [`${scenario}: tagged summary is overwrite-protected`]: (s) => s.isSummaryOverwriteProtected === true,
    });

    const faultTagged = parseRunSettings(scenario, { LT_FAULT: 'p95', LT_MEASURE_TAG: 'before' });
    check(faultTagged, {
      [`${scenario}: tagged fault run name is not in whitelist`]: (s) =>
        fileNameOf(s.summaryPath).startsWith('measure-before-') && !whitelist.includes(fileNameOf(s.summaryPath)),
    });
  }

  const invalidTags = ['a/b', '../x', 'Before', 'a_b', 'a'.repeat(33), ' ', 'a b'];
  for (const tag of invalidTags) {
    const settings = parseRunSettings('seat-ticket', { ...OFFICIAL_ENV, LT_MEASURE_TAG: tag });
    check(settings, {
      [`invalid tag ${JSON.stringify(tag)} produces error`]: (s) => s.errors.some((e) => e.includes('LT_MEASURE_TAG')),
      [`invalid tag ${JSON.stringify(tag)} summary name contains no raw tag and stays outside whitelist`]: (s) =>
        !s.summaryPath.includes(tag) && !whitelist.includes(fileNameOf(s.summaryPath)) && s.summaryPath.lastIndexOf('/') === '/output'.length,
    });
  }

  check(parseRunSettings('seat-ticket', { ...OFFICIAL_ENV, LT_MEASURE_TAG: 'a'.repeat(32) }), {
    '32-char tag is accepted': (s) => s.errors.length === 0,
  });
  check(parseRunSettings('seat-ticket', { ...OFFICIAL_ENV, LT_MEASURE_TAG: '' }), {
    'empty tag means untagged official run': (s) => s.errors.length === 0 && s.measureTag === null && whitelist.includes(fileNameOf(s.summaryPath)),
  });

  // 無競爭基準必須帶標籤（LT-MEASURE-009）。
  check(parseMeasureTag({}, { isRequired: true }), {
    'required tag missing produces error': (r) => r.tag === null && r.errors.some((e) => e.includes('LT_MEASURE_TAG')),
  });
  check(parseMeasureTag({ LT_MEASURE_TAG: '' }, { isRequired: true }), {
    'required tag empty produces error': (r) => r.errors.length > 0,
  });
  check(parseMeasureTag({ LT_MEASURE_TAG: 'before' }, { isRequired: true }), {
    'required tag present is accepted': (r) => r.tag === 'before' && r.errors.length === 0,
  });
  check(parseMeasureTag({}, { isRequired: false }), {
    'optional tag missing is accepted': (r) => r.tag === null && r.errors.length === 0,
  });
}
