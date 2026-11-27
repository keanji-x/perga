import { execFile, spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import { constants } from 'node:fs';
import { access, mkdtemp, readFile, rm } from 'node:fs/promises';
import { request, type IncomingMessage } from 'node:http';
import { createServer } from 'node:https';
import { tmpdir } from 'node:os';
import { delimiter, dirname, join } from 'node:path';
import type { Duplex } from 'node:stream';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const run = promisify(execFile);
const root = fileURLToPath(new URL('../../../', import.meta.url));
const frontend = join(root, 'fe');
const loopback = '127.0.0.1';

export const authOwner = Object.freeze({
  username: 'owner',
  password: 'e2e-owner-password',
  displayName: 'Browser Test Owner',
});

export type AuthBuild = Readonly<{
  dotnet: string;
  hostDll: string;
  web: string;
}>;

async function findDotnet(): Promise<string> {
  for (const folder of (process.env.PATH ?? '').split(delimiter).filter(Boolean)) {
    const candidate = join(folder, 'dotnet');
    try { await access(candidate, constants.X_OK); return candidate; }
    catch { /* Try the next PATH entry. */ }
  }
  throw new Error('Perga auth E2E requires dotnet 11 on PATH.');
}

export async function withAuthBuild(provide: (build: AuthBuild) => Promise<void>): Promise<void> {
  const directory = await mkdtemp(join(tmpdir(), 'perga-auth-build-'));
  try {
    const dotnet = await findDotnet();
    const host = join(directory, 'host');
    const web = join(directory, 'web');
    await run(dotnet, ['build', 'src/Perga.Host/Perga.Host.csproj', '--nologo', '--disable-build-servers',
      '-m:1', '--output', host], {
      cwd: root, env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1' }, timeout: 180_000,
    });
    await run('npm', ['exec', '--', 'vite', 'build', '--outDir', web], { cwd: frontend, timeout: 120_000 });
    await provide({ dotnet, hostDll: join(host, 'Perga.Host.dll'), web });
  } finally { await rm(directory, { recursive: true, force: true }); }
}

async function cli(build: AuthBuild, cwd: string, dbPath: string, args: string[], input?: string): Promise<void> {
  const child = run(build.dotnet, [build.hostDll, 'auth', ...args, '--db-path', dbPath], {
    cwd, env: hostEnvironment(build), timeout: 20_000,
  });
  child.child.stdin?.end(input);
  await child;
}

function hostEnvironment(build: AuthBuild): NodeJS.ProcessEnv {
  return {
    PATH: [dirname(build.dotnet), '/usr/bin', '/bin'].join(delimiter),
    DOTNET_ROOT: process.env.DOTNET_ROOT ?? dirname(build.dotnet),
    DOTNET_PROCESSOR_COUNT: process.env.DOTNET_PROCESSOR_COUNT,
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE: 'false',
  };
}

function listening(child: ChildProcess): Promise<string> {
  return new Promise((resolve, reject) => {
    let logs = '';
    const timer = setTimeout(() => reject(new Error(`Kestrel readiness timed out.\n${logs}`)), 25_000);
    const append = (chunk: Buffer) => {
      logs += chunk.toString();
      const url = /Now listening on: (http:\/\/127\.0\.0\.1:\d+)/.exec(logs)?.[1];
      if (url !== undefined) { clearTimeout(timer); resolve(url); }
    };
    child.stdout?.on('data', append);
    child.stderr?.on('data', append);
    child.once('error', error => { clearTimeout(timer); reject(error); });
    child.once('exit', (code, signal) => {
      clearTimeout(timer);
      reject(new Error(`Kestrel exited (${code ?? signal}).\n${logs}`));
    });
  });
}

async function stop(child: ChildProcess): Promise<void> {
  if (child.pid === undefined || child.exitCode !== null || child.signalCode !== null) return;
  const exited = once(child, 'exit');
  const force = setTimeout(() => child.kill('SIGKILL'), 5000);
  child.kill('SIGTERM');
  try { await exited; }
  finally { clearTimeout(force); }
}

function writeUpgradeResponse(socket: Duplex, response: IncomingMessage): void {
  const headers: string[] = [];
  for (let index = 0; index < response.rawHeaders.length; index += 2) {
    headers.push(`${response.rawHeaders[index]}: ${response.rawHeaders[index + 1]}\r\n`);
  }
  socket.write(`HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\n${headers.join('')}\r\n`);
}

async function startTlsProxy(directory: string, host: () => string) {
  const key = join(directory, 'proxy-key.pem');
  const cert = join(directory, 'proxy-cert.pem');
  await run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
    '-subj', `/CN=${loopback}`, '-addext', `subjectAltName=IP:${loopback}`, '-keyout', key, '-out', cert]);
  const toHost = (incoming: IncomingMessage) => request(host(), {
    path: incoming.url ?? '/',
    method: incoming.method,
    headers: { ...incoming.headers, 'x-forwarded-proto': 'https', 'x-forwarded-for': incoming.socket.remoteAddress },
  });
  const proxy = createServer({ key: await readFile(key), cert: await readFile(cert) }, (incoming, response) => {
    const backend = toHost(incoming);
    backend.on('response', upstream => {
      response.writeHead(upstream.statusCode ?? 502, upstream.headers);
      upstream.pipe(response);
    });
    backend.on('error', () => {
      if (response.headersSent) response.destroy();
      else response.writeHead(502).end();
    });
    response.on('close', () => backend.destroy());
    incoming.pipe(backend);
  });
  proxy.on('upgrade', (incoming, socket, head) => {
    const backend = toHost(incoming);
    socket.on('error', () => backend.destroy());
    backend.on('upgrade', (response, upstream, upstreamHead) => {
      writeUpgradeResponse(socket, response);
      socket.write(upstreamHead);
      upstream.write(head);
      socket.on('error', () => upstream.destroy());
      upstream.on('error', () => socket.destroy());
      socket.on('close', () => upstream.destroy());
      upstream.on('close', () => socket.destroy());
      socket.pipe(upstream).pipe(socket);
    });
    backend.on('response', response => {
      response.resume();
      socket.end(`HTTP/1.1 ${response.statusCode} ${response.statusMessage}\r\nConnection: close\r\n\r\n`);
    });
    backend.on('error', () => socket.destroy());
    backend.end();
  });
  const sockets = new Set<Duplex>();
  proxy.on('connection', socket => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
  });
  proxy.listen(0, loopback);
  await once(proxy, 'listening');
  const address = proxy.address();
  if (address === null || typeof address === 'string') throw new Error('Missing TLS proxy port');
  return {
    origin: `https://${loopback}:${address.port}`,
    close: () => {
      for (const socket of sockets) socket.destroy();
      return new Promise<void>((resolve, reject) => proxy.close(error => error ? reject(error) : resolve()));
    },
  };
}

export type AuthHost = Readonly<{
  origin: string;
  hostOrigin: string;
  revokeSessions(): Promise<void>;
  readLaunchToken(): Promise<string>;
}>;

export async function withAuthHost(build: AuthBuild, proxied: boolean, provide: (host: AuthHost) => Promise<void>,
  firstRun?: 'local' | 'require-auth'): Promise<void> {
  const directory = await mkdtemp(join(tmpdir(), 'perga-auth-host-'));
  let hostOrigin = '';
  let proxy: Awaited<ReturnType<typeof startTlsProxy>> | undefined;
  let child: ChildProcess | undefined;
  try {
    const dbPath = join(directory, 'perga.db');
    if (firstRun === undefined) await cli(build, directory, dbPath, ['init-owner', '--username', authOwner.username,
      '--display-name', authOwner.displayName, '--allow-migrate', '--password-stdin'], `${authOwner.password}\n`);
    if (proxied) proxy = await startTlsProxy(directory, () => hostOrigin);
    child = spawn(build.dotnet, [build.hostDll, '--urls', `http://${loopback}:0`,
      ...(firstRun === 'require-auth' ? ['--require-auth'] : []),
      '--Perga:DataDir', directory, '--Perga:DbPath', dbPath,
      '--Perga:TemplatesDirectory', join(root, 'templates'), '--Perga:StaticWebRootDirectory', build.web,
      '--Perga:OarBridgeScriptPath', join(directory, 'no-provider-bridge.mjs'),
      '--Perga:AutoManageTerminalBridge', 'false', '--Perga:AuthWsRevalidationInterval', '00:00:01',
      ...(proxy === undefined ? [] : ['--Perga:AuthIngressMode', 'Proxied', '--Perga:TrustForwardedHeaders', 'true',
        '--Perga:TrustedProxyAddresses:0', loopback, '--Perga:AuthPublicOrigin', proxy.origin]),
    ], { cwd: directory, env: hostEnvironment(build), stdio: ['ignore', 'pipe', 'pipe'] });
    hostOrigin = await listening(child);
    await provide({ origin: proxy?.origin ?? hostOrigin, hostOrigin,
      revokeSessions: () => cli(build, directory, dbPath, ['revoke-sessions']),
      readLaunchToken: () => readFile(join(directory, 'runtime', 'local-launch-token'), 'utf8'),
    });
  } finally {
    try { if (proxy) await proxy.close(); }
    finally {
      try { if (child) await stop(child); }
      finally { await rm(directory, { recursive: true, force: true }); }
    }
  }
}


export async function nonLoopbackWithoutAuthExit(build: AuthBuild): Promise<{ code: number | string; stderr: string }> {
  const directory = await mkdtemp(join(tmpdir(), 'perga-auth-bind-'));
  try {
    await run(build.dotnet, [build.hostDll, '--urls', 'http://0.0.0.0:0', '--Perga:DataDir', directory], {
      cwd: directory, env: hostEnvironment(build), timeout: 15_000,
    });
    throw new Error('The non-loopback host accepted startup without an owner.');
  } catch (error: unknown) {
    if (typeof error === 'object' && error !== null && 'code' in error && 'stderr' in error &&
      (typeof error.code === 'number' || typeof error.code === 'string') && typeof error.stderr === 'string') {
      return { code: error.code, stderr: error.stderr };
    }
    throw error;
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}
