import { z } from 'zod';
import type { GitHubPreview as GitHubPreviewWire } from './presentation-types.js';
import type { ApiAbortSignal } from '../api/types.js';
import type { GitHubReference } from './issue-url.js';

export const githubPreviewSchema: z.ZodType<GitHubPreviewWire> = z.object({
  kind: z.enum(['issue', 'pull']), number: z.number().int().positive(), title: z.string(),
  state: z.enum(['open', 'closed', 'merged', 'draft']), author: z.string(), labels: z.array(z.string()),
  excerpt: z.string(), changes: z.object({
    additions: z.number().int().nonnegative(), deletions: z.number().int().nonnegative(),
    changed_files: z.number().int().nonnegative(),
  }).nullable(),
});
export type GitHubPreview = GitHubPreviewWire;
export type GitHubPreviewPort = Readonly<{
  read(target: GitHubReference, signal: ApiAbortSignal): Promise<GitHubPreview>;
}>;
