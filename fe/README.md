# Perga frontend

This independent npm project contains platform-neutral contracts in `core/` and
the React client in `web/src/`. Runtime dependencies flow down from app to
features, systems, UI and core. Consumers import their owning module directly.
See [AGENTS.md](AGENTS.md) and [styles](web/src/styles/README.md).

From the repository root:

```sh
(cd fe && npm ci)
(cd fe && npm run dev)
(cd fe && npm run lint && npm test && npm run build)
(cd fe && npm run test:browser)
```

Vite serves `http://localhost:5180/` and proxies `/api` to
`http://127.0.0.1:4041` by default. Set `FE_API_PROXY_TARGET` for another Host;
`FE_DEV_HOST` and `FE_DEV_PORT` configure the dev listener. The build in
`web/dist/` is mounted at `/`. There is no legacy frontend mount.

The Host exposes `/api/v1` and `/api/v1/today`. `core/api/perga-v1.ts` declares
runtime DTO schemas. `npm run gen:api` exports Host OpenAPI and generates
TypeScript from the registered HTTP contracts. `npm run gen:api -- --check`
fails on drift; run it and the contract tests after changing contracts.

Browser state uses the `perga` IndexedDB database and `perga:*` keys. No legacy
browser data is imported. Report source/plugin/wave links use `perga://` and
input forms use `<!-- perga:input-form ... -->`.

## Install in Chrome

Open `/` with HTTPS or HTTP on localhost, then use Chrome's install action.
Launch **Perga** from the system launcher. The manifest identity, start URL and
scope are `/`; installations from a former mount have a different identity.
The app requires a running server and network: there is no service worker or
offline cache. Chrome manages installed name/icon refresh independently.

App icons retain the existing mark geometry, with Perga names and accessibility
text. Regenerate the favicon and PNGs with:

```sh
node tools/pwa/generate-icons.mjs
npx playwright test --config tools/pwa/playwright.config.ts
```

The PWA test builds and serves shipped assets with a closed-port API proxy and
an isolated Chrome profile. It verifies the manifest, root deep links,
installation and standalone launch without reaching a backend.

`@astryxdesign/core` is pinned exactly; review upgrades separately. Module-level
static arrays must be frozen at runtime. Add pure-factory lint exceptions only
with focused positive and negative fixtures; keep architecture gates intact.

## Tooling scope

Legacy development preview servers and the ownership inventory/trailer and test
tier policy tools have been removed. Card layout browser coverage keeps its local
fixture in the owning test. Vitest still discovers platform-independent, web DOM,
browser and coarse-pointer suites through the existing project patterns.

Architecture lint and fixtures, dependency direction checks, style/token checks,
Host contract generation and drift checks, TypeScript and production builds remain
the verification boundaries. Run the commands above plus `npm run gen:api -- --check`
when verifying tooling changes. `yaml` remains a direct dependency for architecture
and style checks. CI changes require L2 review of job/trigger and gate preservation.
