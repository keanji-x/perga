import type { FlowProjection } from '../../fe/core/domain/report-flow.ts';
import type { EditorialListItem } from '../../fe/core/domain/report-editorial.ts';

const NOW = Date.now();
const MIN = 60_000;
const HOUR = 60 * MIN;

export const DEV_DELIVERY_FLOW: FlowProjection = {
  rev: 14,
  filePath: 'flow.json',
  nodes: [
    {
      id: 'spec',
      title: 'Design contract & failure modes',
      source: 'agent',
      whenKind: 'immediate',
      after: [],
      doKind: 'run',
      role: 'lead',
      access: 'read_only',
      provider: 'claude',
      model: 'claude-opus-5-5',
      status: 'done',
      outcome: 'ok',
      outcomeSummary: 'Locked epoch isolation invariants and restart recovery test plan.',
      runCount: 1,
      sessionId: 'ses_spec_01',
      startedAtMs: NOW - 52 * MIN,
      finishedAtMs: NOW - 46 * MIN,
      firedCount: 1,
      waitingOn: [],
      gates: [],
      canceled: false,
      repeat: false,
    },
    {
      id: 'impl-backend',
      title: 'Isolate queued image turns in Kernel',
      source: 'agent',
      whenKind: 'after',
      after: [{ node: 'spec', on: 'ok' }],
      doKind: 'run',
      role: 'impl',
      access: 'read_write',
      provider: 'claude',
      model: 'claude-sonnet-5-5',
      status: 'done',
      outcome: 'ok',
      outcomeSummary: 'Scoped attachment previews to originating epoch; added restart test.',
      runCount: 1,
      sessionId: 'ses_impl_be',
      startedAtMs: NOW - 45 * MIN,
      finishedAtMs: NOW - 29 * MIN,
      firedCount: 1,
      waitingOn: [],
      gates: [
        {
          label: 'dotnet test Perga.sln',
          status: 'pass',
          detail: '412 passed, 0 failed (18.4s)',
          command: 'dotnet test Perga.sln --configuration Release',
          output: 'Passed!  - Failed: 0, Passed: 412, Skipped: 0, Total: 412',
        },
      ],
      canceled: false,
      repeat: false,
    },
    {
      id: 'impl-fe',
      title: 'Regenerate OpenAPI & guard preview scan',
      source: 'agent',
      whenKind: 'after',
      after: [{ node: 'spec', on: 'ok' }],
      doKind: 'run',
      role: 'impl',
      access: 'read_write',
      provider: 'claude',
      model: 'claude-sonnet-5-5',
      status: 'done',
      outcome: 'ok',
      outcomeSummary: 'Updated generated TypeScript contracts and composer attachment tests.',
      runCount: 1,
      sessionId: 'ses_impl_fe',
      startedAtMs: NOW - 45 * MIN,
      finishedAtMs: NOW - 31 * MIN,
      firedCount: 1,
      waitingOn: [],
      gates: [
        {
          label: 'fe lint & vitest',
          status: 'pass',
          detail: '0 lint errors · 198 specs passed',
          command: 'cd fe && npm run lint && npm test',
          output: 'Test Files  64 passed (64)\nTests       198 passed (198)',
        },
      ],
      canceled: false,
      repeat: false,
    },
    {
      id: 'review',
      title: 'Independent cross-model audit',
      source: 'template',
      whenKind: 'after',
      after: [
        { node: 'impl-backend', on: 'ok' },
        { node: 'impl-fe', on: 'ok' },
      ],
      doKind: 'run',
      role: 'review',
      access: 'read_only',
      provider: 'codex',
      model: 'gpt-6.2-astra',
      reasoningEffort: 'high',
      status: 'done',
      outcome: 'ok',
      outcomeSummary: 'Caught stale preview URL leak on mid-turn cancel; verified fix after re-run.',
      runCount: 2,
      sessionId: 'ses_rev_codex',
      startedAtMs: NOW - 28 * MIN,
      finishedAtMs: NOW - 11 * MIN,
      firedCount: 2,
      waitingOn: [],
      gates: [],
      canceled: false,
      repeat: false,
    },
    {
      id: 'ci-watch',
      title: 'Wait for PR #180 CI checks',
      source: 'agent',
      whenKind: 'after',
      after: [{ node: 'review', on: 'ok' }],
      doKind: 'watch',
      status: 'running',
      outcome: 'none',
      outcomeSummary: 'Waiting on GitHub Actions from the local Host before waking the Lead.',
      runCount: 1,
      startedAtMs: NOW - 9 * MIN,
      firedCount: 1,
      waitingOn: [],
      gates: [],
      watchProgress: {
        probeKind: 'prChecks',
        label: 'PR #180 · macOS arm64 + Linux x64',
        passedChecks: 5,
        totalChecks: 6,
        lastCheckedAtMs: NOW - MIN,
        startedAtMs: NOW - 9 * MIN,
        detail: 'Waiting on Package macOS Apple Silicon',
      },
      canceled: false,
      repeat: false,
    },
  ],
  archived: [
    {
      id: 'pre-study',
      title: 'Audit existing attachment lifecycle',
      doKind: 'run',
      status: 'done',
      outcome: 'ok',
      outcomeSummary: 'Mapped 4 call paths across TrackActor, Storage, and Host endpoints.',
      runCount: 1,
      model: 'claude-opus-5-5',
      finishedAtMs: NOW - 2 * HOUR,
      archivedAtMs: NOW - HOUR,
    },
  ],
};

/** Condensed excerpt of flow.json showing the key cross-model & background-wait nodes. */
export const DEV_DELIVERY_FLOW_JSON_EXCERPT = `{
  "rev": 14,
  "nodes": [
    // ... spec + impl-fe omitted for brevity
    {
      "id": "impl-backend",
      "when": { "after": ["spec"] },
      "do": {
        "kind": "run",
        "provider": "claude",
        "model": "claude-sonnet-5-5",
        "gate": "dotnet test Perga.sln"
      }
    },
    {
      "id": "review",
      "when": { "after": ["impl-backend", "impl-fe"] },
      "do": {
        "kind": "run",
        "provider": "codex",
        "model": "gpt-6.2-astra",
        "access": "read_only"
      }
    },
    {
      "id": "ci-watch",
      "when": { "after": ["review"] },
      "do": { "kind": "watch", "prChecks": 180 }
    }
  ]
}`;

export const EQUITY_VALUATION_AXIS = {
  ticker: 'NVDA',
  unit: '$',
  current: {
    value: 138.4,
    display: '$138.40',
    label: 'Spot',
  },
  points: [
    {
      value: 105,
      display: '$105',
      emphasis: false,
      note: 'Bear case · 22x FY27 EPS if hyperscaler CapEx normalizes and custom ASIC share rises.',
    },
    {
      value: 155,
      display: '$155',
      emphasis: true,
      note: 'Base case · 30x FY27 EPS assuming Blackwell rack ramp holds >73% gross margin.',
    },
    {
      value: 192,
      display: '$192',
      emphasis: false,
      note: 'Bull case · 35x FY27 EPS on sovereign AI demand + networking attach expansion.',
    },
  ],
};

export const EQUITY_CHRONICLE_ITEMS: readonly EditorialListItem[] = [
  {
    id: 'evt-q3',
    date: '2026-11-19',
    title: 'Q3 FY27 Earnings & Margin Check',
    status: 'done',
    badge: 'Beat',
    tone: 'good',
    meta: 'codex · gpt-6.2-astra · read-only review',
    summary: 'Data-center revenue beat pre-committed expectations; gross margin landed inline, keeping base thesis intact while tightening falsification threshold at 72.0%.',
    table: {
      columns: [
        { key: 'metric', label: 'Metric' },
        { key: 'expected', label: 'Prior Expectation' },
        { key: 'actual', label: 'Actual' },
        { key: 'status', label: 'Verdict' },
      ],
      rows: [
        {
          metric: 'Data Center Revenue',
          expected: '$33.5B (+11% QoQ)',
          actual: '$35.1B (+16% QoQ)',
          status: { text: 'Beat', tone: 'good', check: true },
        },
        {
          metric: 'GAAP Gross Margin',
          expected: '73.5% ± 40 bps',
          actual: '73.4%',
          status: { text: 'Inline', tone: 'accent' },
        },
        {
          metric: 'Q4 Revenue Guide',
          expected: '$37.2B',
          actual: '$38.5B',
          status: { text: 'Beat', tone: 'good', check: true },
        },
      ],
    },
  },
  {
    id: 'evt-supply',
    date: '2026-12-08',
    title: 'CoWoS-L Packaging Capacity & Lead-Time Check',
    status: 'pending',
    badge: 'Scheduled',
    tone: 'neutral',
    meta: 'at 2026-12-08 09:00 EST',
    summary: 'Cross-check TSMC monthly sales and ODM rack shipment notes against Q4 supply bottleneck hypothesis.',
    table: {
      columns: [
        { key: 'metric', label: 'Metric' },
        { key: 'expected', label: 'Prior Expectation' },
        { key: 'status', label: 'Verdict' },
      ],
      rows: [
        {
          metric: 'Rack Shipment Cadence',
          expected: '≥ 18k NVL72 racks in Q4',
          status: { text: 'Pending', tone: 'neutral' },
        },
      ],
    },
  },
];

export const MEMORY_PINNED_DATA = {
  kind: 'pinned',
  entries: [
    {
      ref: 'area/auth-boundaries',
      title: 'Cleartext HTTP stays loopback-only',
      summary: 'Non-loopback HTTP requires explicit opt-in; owner login remains mandatory.',
      body: 'Keep `TransportSecurityMiddleware` fail-closed on LAN or proxied requests unless `PERGA_AUTH_ALLOW_INSECURE_HTTP=true` is set.',
      tags: ['security', 'host'],
      sources: ['trk_auth_0168'],
      updatedAtMs: NOW - 16 * HOUR,
    },
  ],
};

export const MEMORY_TOPICS_DATA = {
  kind: 'topics',
  entries: [
    {
      ref: 'area/queued-turn-isolation',
      title: 'Queued multimodal turns bind to originating epoch',
      summary: 'Prevent attachment preview URLs from leaking across canceled turns.',
      body: 'Bind media attachment tokens to `(sessionId, epoch)` at enqueue time in `TrackActor.SessionInputs`. Verify with a fresh-host restart integration test.',
      tags: ['chat', 'race'],
      sources: ['trk_img_0180'],
      updatedAtMs: NOW - 3 * HOUR,
    },
    {
      ref: 'global/review-discipline',
      title: 'Enforce second-provider review on non-trivial diffs',
      summary: 'Never let the implementing session grade its own patch.',
      body: 'Route `review` tasks to a distinct model with `access: "read_only"` backed by a local test gate.',
      tags: ['workflow'],
      sources: ['trk_core_0155'],
      updatedAtMs: NOW - 96 * HOUR,
    },
  ],
};
