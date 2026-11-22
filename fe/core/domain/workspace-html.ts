/**
 * The one page-name rule (shared with the Track raw route): the last segment is `{stem}.html` or
 * `{stem}.htm`, case-insensitive, with a non-empty stem. A bare `.html` is a dotfile, not a page.
 */
export function isHtmlPageName(path: string): boolean {
  const name = (path.split('/').at(-1) ?? '').toLowerCase();
  return ['.html', '.htm'].some((extension) => name.length > extension.length && name.endsWith(extension));
}

/** Unicode `White_Space`, the set used for workspace path trimming. */
const SURROUNDING_WHITE_SPACE = /^\p{White_Space}|\p{White_Space}$/u;

/**
 * A path the Track raw route serves as a page exactly as named: a page name, and no surrounding
 * whitespace (the route trims it, so such a path would name a different file).
 */
export function isHtmlPagePath(path: string): boolean {
  return !SURROUNDING_WHITE_SPACE.test(path) && isHtmlPageName(path);
}
