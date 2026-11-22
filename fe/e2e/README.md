# Frontend end-to-end tests

Run `npm run e2e` from `fe/` against a dedicated Perga Host at
`http://127.0.0.1:4041`. Initialize its test owner and set `FE_E2E_USERNAME`
and `FE_E2E_PASSWORD` to the test credentials. Playwright starts Vite on port
5180. Override the Host with `FE_API_PROXY_TARGET` and the frontend port with
`FE_DEV_PORT`.

Run `npx playwright test e2e/auth.spec.ts` for the self-contained auth suite.
It builds the web app and Perga Host once per worker and uses a fresh SQLite
database and loopback listener for each test. Put .NET 11 and OpenSSL on PATH
and install Playwright Chromium before running it.

Seeds and assertions use `/api/v1` and the schemas in `core/api/perga-v1.ts`.
Track creation returns an aggregate snapshot; conversation creation returns a
session creation receipt. Message reads use session items, and sends use
`content` with an `Idempotency-Key` header. Tests clean up their Areas.

Conversation execution cases require a controlled provider that completes turns
and supports the scripted `fake-reply:` messages. Run them
in a dedicated test deployment, never against a shared provider installation.
Retired settings, plugin installation, attachment, archive, raw report append,
and conversation run-conflict compatibility cases have been removed.
