/**
 * `perga:` key literals belong in core/keys so renames have one owner.
 * URI schemes (`perga://`) are report contracts, not storage keys.
 * Known escape: separated concatenation such as `'perga' + ':x'` is not rejected.
 */

const pergaKey = /^perga:(?!\/\/)/;

/** @type {import('eslint').Rule.RuleModule} */
export const noPergaKeyOutsideCoreKeys = {
  meta: {
    type: 'problem',
    schema: [{ type: 'object', additionalProperties: false, properties: {
      nonStorageLiterals: { type: 'array', items: { type: 'object', additionalProperties: false,
        required: ['file', 'values'], properties: { file: { type: 'string' }, values: { type: 'array', items: { type: 'string' } } } } },
    } }],
    messages: { key: 'perga key literals belong in core/keys: {{source}}' },
  },
  create(context) {
    const filename = context.filename.replaceAll('\\', '/');
    if (filename.includes('/core/keys/') || /\/core\/keys\.[cm]?[jt]sx?$/.test(filename)) return {};
    // Exact file + value exceptions distinguish independently published message/tool
    // identifiers from storage keys; another value in the same file still fails.
    /** @type {Array<{file: string, values: string[]}>} */
    const nonStorage = context.options[0]?.nonStorageLiterals ?? [];
    const isNonStorage = (/** @type {string} */ value) => nonStorage.some(entry => filename.endsWith(`/${entry.file}`) && entry.values.includes(value));
    const report = (/** @type {any} */ node) => context.report({
      node, messageId: 'key', data: { source: context.sourceCode.getText(node) },
    });
    return {
      Literal(/** @type {any} */ node) {
        if (typeof node.value === 'string' && pergaKey.test(node.value) && !isNonStorage(node.value)) report(node);
      },
      TemplateLiteral(/** @type {any} */ node) {
        const head = node.quasis[0]?.value.cooked ?? node.quasis[0]?.value.raw ?? '';
        if (pergaKey.test(head) && !(node.expressions.length === 0 && isNonStorage(head))) report(node);
      },
    };
  },
};
