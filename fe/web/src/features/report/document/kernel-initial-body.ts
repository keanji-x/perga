/* Canonical initial report body skeleton (#1185). Test-only. Functions, not module-level bindings:
   module runtime state is forbidden by `architecture/`. */

/** Closes the contract comment; a blank line then starts the first section. */
const CLOSE = '-->\n\n';

/** The exact body a freshly minted track's report card holds. */
export function initialBody(): string {
  return [
    '<!-- neige:contract v1 -->',
    '<!-- 报告维护契约（渲染时被丢弃，读 body 源码的主体看得到）',
    '',
    '这份报告自带的结构就是规则：维护它，不要重写它。',
    '',
    '写作方式：散文正文控制在 1000 字以内。',
    '-->',
    '',
    '# 概要',
    '',
    '# 待你定',
    '',
    '# 已完成',
    '',
    '# 决策',
    '',
  ].join('\n');
}

/** `[contract, ...sections]` — the same five slices `split_body` derives, split at the comment close and at each line-initial `# `. */
export function splitInitialBody(): string[] {
  const body = initialBody();
  const close = body.indexOf(CLOSE);
  if (close < 0) throw new Error('the shipped default.md no longer closes the contract comment');
  const contract = body.slice(0, close + CLOSE.length);
  const sections = body
    .slice(close + CLOSE.length)
    .split(/^(?=# )/m)
    .filter((s) => s.length > 0);
  return [contract, ...sections];
}
