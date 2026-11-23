import { EN_MESSAGES, type MessageCatalog } from './i18n-en.js';

export { EN_MESSAGES, type MessageCatalog };

/** Active read-only message catalog. Structured for future locale switching without mutable module state. */
export const MESSAGES: MessageCatalog = EN_MESSAGES;

/** Interpolate `{key}` placeholders in a catalog template string. */
export function formatMessage(
  template: string,
  params: Readonly<Record<string, string | number>>,
): string {
  return template.replace(/\{(\w+)\}/g, (match, key: string) =>
    Object.hasOwn(params, key) ? String(params[key]) : match);
}
