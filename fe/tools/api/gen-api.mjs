import { generateWire } from './schema-types.mjs';
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const check = process.argv.slice(2).includes('--check');
if (process.argv.slice(2).some(arg => arg !== '--check')) throw new Error('Usage: gen:api [--check]');
const scratch = resolve(root, 'fe/node_modules/.cache/perga-api');
mkdirSync(scratch, { recursive: true });
const temporary = resolve(scratch, 'openapi.json');
const localDotnet = resolve(homedir(), '.dotnet/dotnet');
const dotnet = process.env.DOTNET_HOST_PATH ?? (existsSync(localDotnet) ? localDotnet : 'dotnet');
const result = spawnSync(dotnet, ['run', '--project', 'src/Perga.Host', '--no-launch-profile', '--no-restore', '--disable-build-servers', '-p:BuildInParallel=false', '-p:UseSharedCompilation=false', '--', '--export-openapi', temporary], {
  cwd: root, stdio: 'inherit', env: { ...process.env, DOTNET_USE_POLLING_FILE_WATCHER: '1' },
});
if (result.error) throw result.error;
if (result.status !== 0) throw new Error(`Host export failed: ${result.status}`);
const documentText = readFileSync(temporary, 'utf8');
const document = JSON.parse(documentText);
if (document.openapi !== '3.1.0' || Object.keys(document.paths).length === 0) throw new Error('Empty or invalid Host document');

const wireText = generateWire(document.components.schemas);
for (const [name, text] of [['openapi.json', documentText], ['wire.ts', wireText]]) {
  const target = resolve(root, 'fe/core/api/generated', name);
  if (check) {
    if (!existsSync(target) || readFileSync(target, 'utf8') !== text) throw new Error(`${name} drifted from Host. Run npm run gen:api.`);
  } else writeFileSync(target, text);
}
console.log(check ? 'Host OpenAPI and TypeScript: no drift' : 'Generated Host OpenAPI and TypeScript');
