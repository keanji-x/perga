import { join } from 'node:path';

const githubPath = './github-squash.ts';
const { githubOwnershipRequest, githubSquashCommits } = await import(githubPath);
const validatorPath = './validator.ts';
const { gitOwnershipCommits, ownershipCommitsForEvent, repositoryFiles, resolveOwnershipBase, validateOwnership } = await import(validatorPath);
const { auditStyleRepository } = await import('../styles/repository-check.mjs');
const { ownershipManifest } = await import('../../ownership-manifest.mjs');

{
  const feRoot = join(import.meta.dirname, '../..');
  const head = process.env.OWNERSHIP_HEAD_SHA ?? 'HEAD';
  const base = resolveOwnershipBase(
    join(feRoot, '..'),
    process.env.OWNERSHIP_BASE_SHA ?? '',
    head,
    process.env.OWNERSHIP_EVENT_NAME,
    process.env.OWNERSHIP_PUSH_FORCED === 'true',
  );
  const commits = await ownershipCommitsForEvent(process.env.OWNERSHIP_EVENT_NAME,
    () => gitOwnershipCommits(join(feRoot, '..'), base, head), ownershipManifest,
    /** @param {import('./validator.ts').OwnershipCommit} commit */ (commit) => {
      const repository = process.env.GITHUB_REPOSITORY ?? '';
      return githubSquashCommits(commit, repository, githubOwnershipRequest({
        repository, token: process.env.GITHUB_TOKEN ?? '',
      }));
    });
  const repositoryViolations = validateOwnership(
    ownershipManifest, repositoryFiles(join(feRoot, '..')),
    process.env.OWNERSHIP_EVENT_NAME === 'push' ? [] : commits,
  );
  if (repositoryViolations.length) {
    throw new Error(`repository ownership audit failed:\n${repositoryViolations
      .map(/** @param {{message: string}} violation */ (violation) => violation.message).join('\n')}\n`
      + 'if the commit is already on main, run `git fetch origin main`');
  }
  console.log(`ownership manifest: ${ownershipManifest.length} entries, complete coverage, no overlaps`);
  const styleViolations = auditStyleRepository(join(import.meta.dirname, '../..'));
  if (styleViolations.length) throw new Error(`style repository audit failed:\n${styleViolations.join('\n')}`);
  console.log('style manifests, CSS Module layers, and data-* attributes: valid');

}
