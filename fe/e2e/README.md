# Frontend end-to-end tests

Run `npm run e2e` from `fe/` against a Perga Host at `http://127.0.0.1:4041`.
Playwright starts Vite on port 5180. Override the Host with `FE_API_PROXY_TARGET`
and the frontend port with `FE_DEV_PORT`.

Seeds and assertions use `/api/v1` and the schemas in `core/api/perga-v1.ts`.
Track creation returns an aggregate snapshot; conversation creation returns a
session creation receipt. Message reads use session items, and sends use
`content` with an `Idempotency-Key` header. Tests clean up their Areas.

Conversation execution cases require a controlled provider that completes turns
and supports the scripted `fake-reply:` messages. Run them
in a dedicated test deployment, never against a shared provider installation.
Retired settings, plugin installation, attachment, archive, raw report append,
and conversation run-conflict compatibility cases have been removed.
