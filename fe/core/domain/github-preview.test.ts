import { describe,expect,it } from 'vitest';
import { githubPreviewSchema } from './github-preview.js';
import { parseGitHubReferenceUrl } from './issue-url.js';

describe('GitHub preview references', () => {
  it.each([
    'http://github.com/o/r/pull/1', 'https://github.com.evil.test/o/r/pull/1',
    'https://user@github.com/o/r/pull/1', 'https://github.com:443/o/r/pull/1',
    'https://github.com/o%2Fr/r/pull/1', 'https://github.com/o/../pull/1',
    'https://github.com/o/r/pull/1/files', 'https://github.com/o/r/pull/01',
    'https://github.com/o/r/pull/0', 'https://github.com/o/r/pull/9007199254740992',
    'javascript:alert(1)', 'https://github.com/o/r/issues/1/pull/2',
  ])('rejects %s', (url) => expect(parseGitHubReferenceUrl(url)).toBeNull());
  it('requires the full response contract', () => {
    expect(githubPreviewSchema.safeParse({ title: 'Title' }).success).toBe(false);
  });
});
