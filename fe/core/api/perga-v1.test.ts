import { describe, expect, it } from 'vitest';

import { bootstrapOperation, whoamiOperation } from './auth.js';
import {
  bootstrapDtoSchema,
  decodePergaEventEnvelope,
  trackAggregateSnapshotDtoSchema,
} from './perga-v1.js';
import { decodeWireEvent } from './schemas.js';
import {
  areaCreationCapabilityOperation,
  areaFoldersOperation,
  areaListOperation,
  createAreaOperation,
  deleteAreaOperation,
  updateAreaOperation,
} from '../domain/area.js';
import { agentProvidersOperation, managedProvidersOperation } from '../domain/agent-providers.js';
import { answerAskOperation, openAsksOf } from '../domain/ask.js';
import {
  compactPlannerOperation,
  createTrackConversationOperation,
  interruptPlannerOperation,
  modelCatalogOperation,
  setPlannerModelOperation,
  sendPlannerInputOperation,
  trackConversationsOperation,
} from '../domain/conversation.js';
import { buildConversationTranscript, conversationItemsOperation, conversationRunOperation } from '../domain/conversation-display.js';
import {
  listTrackWorkspaceDirectoryOperation,
  readTrackWorkspaceFileOperation,
  trackVcsDiffOperation,
} from '../domain/fs.js';
import { plannerSwitchOperation } from '../domain/planner-switch.js';
import {
  readTrackReport,
  readTrackReportOperation,
  trackTaskVerdictsOperation,
  updateTrackReportBlockOperation,
} from '../domain/report.js';
import { taskAttemptsOperation } from '../domain/task-recovery.js';
import { templateDetailOperation } from '../domain/template.js';
import {
  createTerminalCardOperation,
  createTrackOperation,
  deleteTerminalOperation,
  deleteTrackOperation,
  listTrackTerminalsOperation,
  trackActivitiesFrom,
  trackDetailOperation,
  tracksInAreaOperation,
  trackTemplatesOperation,
  updateTrackOperation,
} from '../domain/track.js';
import { parsePergaMarkdownBlocks } from '../markdown/public.js';

describe('Perga /api/v1 protocol contracts', () => {
  it('decodes historical mail and preserves the parent ID of a reply', () => {
    const envelope = { eventId: 8, areaId: 'area', trackId: 'receiver', kind: 'mail.received', createdAtMs: 1 };
    const mail = { id: 'mail', fromTrackId: 'sender', toTrackId: 'receiver', summary: 'Reply', body: 'Details', createdAtMs: 1 };
    expect(decodePergaEventEnvelope({ ...envelope, payload: mail })).toMatchObject({ status: 'ready' });
    expect(decodePergaEventEnvelope({ ...envelope, payload: { ...mail, replyToMailId: 'parent' } }))
      .toMatchObject({ status: 'ready', event: { payload: { replyToMailId: 'parent' } } });
  });

  const sampleBootstrap = {
    version: '0.1.0',
    providers: [
      { provider: 'codex', available: true, version: '0.45.0', reason: null, models: ['gpt-5', 'o3'] },
      { provider: 'claude', available: false, version: null, reason: 'claude CLI not found', models: [] },
      { provider: 'acp:antigravity', available: true, version: '1.2.0', reason: null, models: ['gemini-3.1-pro', 'gemini-3-flash'] },
      { provider: 'acp:opencode', available: true, version: '0.8.0', reason: null, models: ['deepseek/deepseek-r1'] },
    ],
    templates: [
      {
        id: 'feature-dev',
        title: 'Feature Development',
        description: 'End-to-end feature track',
        defaultPlugins: ['github'],
        inputsSchema: { type: 'object', properties: { branch: { type: 'string' } } },
        sections: [{ h1: 'Plan', omitIfEmpty: false, editableBy: ['lead', 'human'] }],
        maxProseChars: 8000,
        requireGateForReadWrite: true,
        workspaceDriver: 'vcs-workspace',
        workspaceLowerCacheDirs: ['fe/node_modules'],
        workspaceCreateCmd: null,
        workspaceCloseCmd: null,
        afterWorkerDoneCmd: null,
        schedules: [],
        initialReportMarkdown: '# Plan\n\nInitial outline.',
      },
    ],
    areas: [
      {
        id: 'area_1',
        name: 'Perga Core',
        color: '#2563eb',
        defaultCwd: '/repo',
        folders: ['/repo', '/docs'],
        sortOrder: 1,
        createdAtMs: 1000,
        updatedAtMs: 2000,
      },
    ],
  };

  it('decodes GET /api/v1/bootstrap for auth, providers, models, and templates', () => {
    expect(bootstrapOperation().path).toBe('/api/v1/bootstrap');
    expect(whoamiOperation().path).toBe('/api/v1/bootstrap?scope=whoami');
    const parsed = bootstrapDtoSchema.parse(sampleBootstrap);
    expect(parsed.version).toBe('0.1.0');

    const cap = areaCreationCapabilityOperation();
    expect(cap.path).toBe('/api/v1/bootstrap');
    expect(cap.responseSchema.parse(sampleBootstrap)).toBe('supported');

    const providers = agentProvidersOperation(false).responseSchema.parse(sampleBootstrap);
    expect(providers).toHaveLength(4);
    expect(providers[0]).toMatchObject({ provider: 'codex', status: 'ready' });
    expect(providers[1]).toMatchObject({ provider: 'claude', status: 'unavailable', reason: 'claude CLI not found' });
    expect(providers[2]).toMatchObject({ provider: 'antigravity', status: 'ready' });
    expect(providers[3]).toMatchObject({ provider: 'opencode', status: 'ready' });

    const managed = managedProvidersOperation(true);
    expect(managed.path).toBe('/api/v1/bootstrap?scope=managed&refresh=true');
    expect(managed.responseSchema.parse(sampleBootstrap)).toHaveLength(4);

    const catalog = modelCatalogOperation({ kind: 'provider', provider: 'codex' }).responseSchema.parse(sampleBootstrap);
    expect(catalog.models.map((m) => m.model)).toEqual(['gpt-5', 'o3']);
    const agyCatalog = modelCatalogOperation({ kind: 'provider', provider: 'antigravity' }).responseSchema.parse(sampleBootstrap);
    expect(agyCatalog.models.map((m) => m.model)).toEqual(['gemini-3.1-pro', 'gemini-3-flash']);
    const opencodeCatalog = modelCatalogOperation({ kind: 'provider', provider: 'opencode' }).responseSchema.parse(sampleBootstrap);
    expect(opencodeCatalog.models.map((m) => m.model)).toEqual(['deepseek/deepseek-r1']);

    const templatesOp = trackTemplatesOperation();
    expect(templatesOp.path).toBe('/api/v1/templates');
    const templates = templatesOp.responseSchema.parse(sampleBootstrap.templates);
    expect(templates[0]).toMatchObject({ id: 'feature-dev', title: 'Feature Development' });

    const detail = templateDetailOperation('feature-dev').responseSchema.parse(sampleBootstrap.templates);
    expect(detail.body).toContain('# Plan');
  });

  it('preserves rich provider model metadata and selects the advertised default', () => {
    const modelCatalog = [
      { model: 'first', displayName: 'First model', description: 'First description', isDefault: false,
        defaultReasoningEffort: null, supportedReasoningEfforts: [] },
      { model: 'preferred', displayName: 'Preferred model', description: 'Provider description', isDefault: true,
        defaultReasoningEffort: 'high', supportedReasoningEfforts: [
          { reasoningEffort: 'low', description: 'Fast' },
          { reasoningEffort: 'high', description: 'Thorough' },
        ] },
    ];
    const bootstrap = { ...sampleBootstrap, providers: [
      { provider: 'codex', available: true, models: ['legacy-only'], modelCatalog },
      { provider: 'acp:opencode', available: true, models: ['other-provider'] },
    ] };
    expect(bootstrapDtoSchema.parse(bootstrap).providers[0].modelCatalog).toEqual(modelCatalog);
    for (const scope of [
      { kind: 'provider' as const, provider: 'codex' as const },
      { kind: 'card' as const, cardId: 'session/a', provider: 'codex' as const },
    ]) {
      const operation = modelCatalogOperation(scope);
      const catalog = operation.responseSchema.parse(bootstrap);
      expect(catalog.models).toEqual(modelCatalog.map(entry => ({
        id: entry.model, model: entry.model, resolved_model: null,
        display_name: entry.displayName, description: entry.description, is_default: entry.isDefault,
        default_reasoning_effort: entry.defaultReasoningEffort,
        supported_reasoning_efforts: entry.supportedReasoningEfforts.map(effort => ({
          reasoning_effort: effort.reasoningEffort, description: effort.description,
        })),
      })));
      expect(catalog).toMatchObject({ source: 'live', default_source: 'config_read', default: {
        model: 'preferred', reasoning_effort: 'high', supported_reasoning_efforts: [
          { reasoning_effort: 'low', description: 'Fast' },
          { reasoning_effort: 'high', description: 'Thorough' },
        ],
      } });
      expect(operation.responseSchema.parse({ ...bootstrap, providers: [{ ...bootstrap.providers[0], available: false }] }))
        .toMatchObject({ source: 'unavailable', models: catalog.models, default: catalog.default });
    }
  });

  it('scopes a card catalog to its provider and reports provider availability', () => {
    const scope = { kind: 'card' as const, cardId: 'session/a', provider: 'antigravity' as const };
    const operation = modelCatalogOperation(scope);
    expect(operation.responseSchema.parse(sampleBootstrap)).toMatchObject({
      source: 'live', models: [{ model: 'gemini-3.1-pro' }, { model: 'gemini-3-flash' }],
    });
    expect(modelCatalogOperation({ ...scope, provider: 'claude' }).responseSchema.parse(sampleBootstrap).source).toBe('unavailable');
    expect(modelCatalogOperation({ ...scope, provider: 'grok' }).responseSchema.parse(sampleBootstrap).source).toBe('unavailable');
    expect(operation.responseSchema.parse({ version: 'test', providers: [{ provider: 'acp:antigravity', available: true, models: [] }] }))
      .toMatchObject({ source: 'live', models: [] });
    expect(operation.responseSchema.safeParse({ models: [], source: 'live' }).success).toBe(false);
  });

  it('updates a session model with the v1 request and session response', () => {
    const operation = setPlannerModelOperation('session/a', { model: 'chosen', reasoning_effort: null });
    expect(operation.method).toBe('PUT');
    expect(operation.path).toBe('/api/v1/sessions/session%2Fa/model');
    expect(operation.body).toEqual({ model: 'chosen', reasoningEffort: null });
    expect(operation.responseSchema.parse({
      id: 'session/a', trackId: 'track', provider: 'codex', model: 'chosen', createdAtMs: 1, updatedAtMs: 2,
    })).toMatchObject({ id: 'session/a', model: 'chosen' });
  });

  it('maps Area CRUD operations to /api/v1/areas with camelCase bodies and clearDefaultCwd', () => {
    const listOp = areaListOperation();
    expect(listOp).toMatchObject({ method: 'GET', path: '/api/v1/areas' });
    const areas = listOp.responseSchema.parse(sampleBootstrap.areas);
    expect(areas[0]).toMatchObject({ id: 'area_1', sort: 1, default_cwd: '/repo' });

    const createOp = createAreaOperation({ name: 'Infra', color: '#10b981', default_cwd: '/infra', sort: 3 }, 'idem-a1');
    expect(createOp).toMatchObject({
      method: 'POST',
      path: '/api/v1/areas',
      headers: { 'Idempotency-Key': 'idem-a1' },
      body: { name: 'Infra', color: '#10b981', defaultCwd: '/infra', sortOrder: 3 },
    });

    const patchClearOp = updateAreaOperation('area_1', { default_cwd: null });
    expect(patchClearOp).toMatchObject({
      method: 'PATCH',
      path: '/api/v1/areas/area_1',
      body: { defaultCwd: null, clearDefaultCwd: true },
    });

    const folders = areaFoldersOperation('area_1').responseSchema.parse(sampleBootstrap.areas);
    expect(folders.map((f) => f.path)).toEqual(['/repo', '/docs']);

    expect(deleteAreaOperation('area_1')).toMatchObject({ method: 'DELETE', path: '/api/v1/areas/area_1' });
  });

  it('maps Track aggregate snapshot, tasks, report blocks, asks, and planner switch to /api/v1', () => {
    const snapshotRaw = {
      track: {
        id: 'trk_1',
        areaId: 'area_1',
        parentTrackId: null,
        parentAttemptId: null,
        title: 'Migrate FE',
        cwd: '/repo/.worktrees/trk_1',
        workspaceDriver: 'vcs-workspace',
        workspaceSpec: { driverId: 'vcs-workspace', logicalRoot: '/repo', mergedCwd: '/repo/.worktrees/trk_1' },
        leadSessionId: 'ses_lead',
        templateId: 'feature-dev',
        templateInputs: {},
        templateState: {},
        enabledPlugins: ['github'],
        closedAtMs: null,
        pinnedAtMs: 1500,
        sortOrder: 2,
        createdAtMs: 1000,
        updatedAtMs: 2500,
      },
      report: {
        docRev: 4,
        summary: 'Stage 2 migration',
        leadingComment: '',
        blocks: [
          { id: 'b_prose1', rev: 2, kind: 'prose', sectionH1: 'Plan', markdown: '# Plan\n\nShip v1.', payload: null },
          {
            id: 'b_task1',
            rev: 1,
            kind: 'task',
            sectionH1: 'Plan',
            markdown: JSON.stringify({
              task_key: 'build-fe',
              kind: 'agent',
              provider: 'codex',
              access_mode: 'read_write',
              ready: true,
              goal: 'Update FE endpoints',
            }),
            payload: null,
          },
        ],
      },
      tasks: [
        {
          id: 'tsk_1',
          trackId: 'trk_1',
          blockId: 'b_task1',
          taskKey: 'build-fe',
          generation: 1,
          kind: 'agent',
          provider: 'codex',
          access: 'read_write',
          ready: true,
          dependsOn: [],
          goalOrCmd: 'Update FE endpoints',
          context: null,
          acceptance: null,
          gate: null,
          status: 'running',
          statusDetail: null,
          semanticVerdict: 'none',
          semanticNote: null,
          latestAttemptId: 'att_1',
          createdAtMs: 1200,
          updatedAtMs: 2400,
        },
      ],
      sessions: [
        {
          id: 'ses_lead',
          trackId: 'trk_1',
          isLead: true,
          boundTaskId: null,
          boundAttemptId: null,
          title: 'Lead',
          provider: 'codex',
          model: 'gpt-5',
          nativeThreadId: 'thr_1',
          epoch: 2,
          status: 'running',
          contextUsageRatio: 0.42,
          archivedAtMs: null,
          createdAtMs: 1000,
          updatedAtMs: 2500,
        },
      ],
      openAsks: [
        {
          id: 'ask_abc123',
          trackId: 'trk_1',
          status: 'open',
          questions: [{ question: 'Approve deployment?', options: ['Approve', 'Deny'], isMultiSelect: false }],
          answer: null,
          createdAtMs: 2300,
          answeredAtMs: null,
        },
      ],
      templateSnapshotSource: null,
    };

    expect(trackAggregateSnapshotDtoSchema.safeParse(snapshotRaw).success).toBe(true);
    expect(tracksInAreaOperation('area_1').path).toBe('/api/v1/areas/area_1/tracks?includeClosed=true');

    const detailOp = trackDetailOperation('trk_1');
    expect(detailOp.path).toBe('/api/v1/tracks/trk_1');
    const detail = detailOp.responseSchema.parse(snapshotRaw);
    expect(detail.track.workspace).toEqual({
      isolation: 'worktree',
      kind: 'attached',
      path: '/repo',
      frozen_at: null,
      worktree: '/repo/.worktrees/trk_1',
    });
    expect(detail.cards.map((c) => c.kind)).toEqual(['track-report', 'codex']);

    const acpSnapshot = {
      ...snapshotRaw,
      sessions: [{ ...snapshotRaw.sessions[0], provider: 'acp:antigravity' }],
    };
    const acpDetail = detailOp.responseSchema.parse(acpSnapshot);
    expect(acpDetail.cards[1]?.payload).toMatchObject({ planner_provider: 'antigravity' });

    const report = readTrackReport(detail.cards);
    expect(report?.docRev).toBe(4);
    expect(report?.blockRevs).toEqual({ b_prose1: 2, b_task1: 1 });
    expect(report?.blocks?.map((b) => b.kind)).toEqual(['prose', 'task']);

    const activity = trackActivitiesFrom(detail.overlays).get('trk_1');
    expect(activity?.working).toBe(true);
    expect(activity?.attention).toBe('input');
    const asks = openAsksOf(activity?.attentionItems ?? []);
    expect(asks[0]).toMatchObject({ askId: 'ask_abc123' });

    const answerOp = answerAskOperation('trk_1', 'ask_abc123', [{ option: 0 }], asks[0]?.questions);
    expect(answerOp).toMatchObject({
      method: 'POST',
      path: '/api/v1/tracks/trk_1/asks/ask_abc123/answer',
      body: { answers: [{ option: 0, value: 'Approve' }] },
    });

    const createOp = createTrackOperation({
      area_id: 'area_1',
      planner_provider: 'codex',
      title: 'New Track',
      cwd: '/repo',
      isolation: 'worktree',
      theme: { fg: [0, 0, 0], bg: [255, 255, 255] },
      first_message: 'Start here',
    }, 'idem-t1');
    expect(createOp.path).toBe('/api/v1/tracks');
    expect(createOp.body).toMatchObject({
      areaId: 'area_1',
      provider: 'codex',
      workspaceDriver: 'vcs-workspace',
      firstMessage: 'Start here',
    });

    const createDirectAcpOp = createTrackOperation({
      area_id: 'area_1',
      planner_provider: 'antigravity',
      cwd: '/repo',
      isolation: 'none',
      theme: { fg: [0, 0, 0], bg: [255, 255, 255] },
    }, 'idem-t2');
    expect(createDirectAcpOp.body).toMatchObject({
      areaId: 'area_1',
      planner_provider: 'antigravity',
      provider: 'acp:antigravity',
      workspaceDriver: 'direct',
    });

    expect(updateTrackOperation('trk_1', { closed: true }).path).toBe('/api/v1/tracks/trk_1');
    expect(deleteTrackOperation('trk_1').path).toBe('/api/v1/tracks/trk_1');

    const switchOp = plannerSwitchOperation('trk_1', {
      target_assistant_card_id: 'ses_sub1',
      expected_planner_card_id: 'ses_lead',
      expected_revision: 4,
    });
    expect(switchOp).toMatchObject({
      method: 'PATCH',
      path: '/api/v1/tracks/trk_1',
      body: { leadSessionId: 'ses_sub1' },
    });

    expect(readTrackReportOperation('trk_1').path).toBe('/api/v1/tracks/trk_1/report');
    expect(trackTaskVerdictsOperation('trk_1').path).toBe('/api/v1/tracks/trk_1/report');
    const blockPut = updateTrackReportBlockOperation('trk_1', 'b_prose1', {
      docRev: 4,
      knownBlockRev: 2,
      kind: 'prose',
      markdown: '# Updated',
    });
    expect(blockPut).toMatchObject({
      method: 'PUT',
      path: '/api/v1/tracks/trk_1/report/blocks/b_prose1',
    });

    const attemptsOp = taskAttemptsOperation('trk_1', 'build-fe');
    expect(attemptsOp.path).toBe('/api/v1/tracks/trk_1/tasks/build-fe/attempts');
    const attemptsView = attemptsOp.responseSchema.parse([
      {
        attemptId: 'att_1',
        taskId: 'tsk_1',
        trackId: 'trk_1',
        sessionId: 'ses_worker1',
        status: 'failed',
        workerResult: null,
        meta: {},
        workspaceLease: null,
        gateVerdict: { passed: false, steps: [{ name: 'lint', exitCode: 1 }] },
        startedAtMs: 1300,
        finishedAtMs: 1900,
      },
    ]);
    expect(attemptsView.current?.status_detail).toBe('Gate step failed: lint');
  });

  it('maps Binary Sessions (Lead + Subagent), Terminals, Files, VCS Diff, and WS envelopes', () => {
    const pageDto = {
      items: [
        { id: 1, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'user', payload: { v: 1, content: 'Hello Perga', triggerKind: 'user' }, createdAtMs: 1000 },
        { id: 2, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'tool_call', payload: { v: 1, callId: 'c1', name: 'perga_report_read', args: {} }, createdAtMs: 1100 },
        { id: 3, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'tool_result', payload: { v: 1, callId: 'c1', output: 'ok', isError: false }, createdAtMs: 1200 },
        { id: 4, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'thinking', payload: { v: 1, text: 'Planning...' }, createdAtMs: 1250 },
        { id: 5, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'assistant_text', payload: { v: 1, text: 'Report inspected.' }, createdAtMs: 1300 },
      ],
      activeTurn: null,
      unknownInput: null,
      session: {
        id: 'ses_lead',
        trackId: 'trk_1',
        isLead: true,
        boundTaskId: null,
        boundAttemptId: null,
        title: 'Lead',
        provider: 'codex',
        model: 'gpt-5',
        nativeThreadId: 'thr_1',
        epoch: 2,
        status: 'idle',
        contextUsageRatio: 0.65,
        archivedAtMs: null,
        createdAtMs: 900,
        updatedAtMs: 1300,
      },
    };

    const itemsOp = conversationItemsOperation('ses_lead', null);
    expect(itemsOp.path).toBe('/api/v1/sessions/ses_lead/items?afterId=0&limit=500&latest=true');
    const page = itemsOp.responseSchema.parse(pageDto);
    const transcript = buildConversationTranscript(page.items, 'owner');
    expect(transcript.some((e) => e.author === 'you' && e.text === 'Hello Perga')).toBe(true);
    expect(transcript.some((e) => e.author === 'activity' && e.verb === 'Read report')).toBe(true);
    expect(transcript.some((e) => e.author === 'agent' && e.text === 'Report inspected.')).toBe(true);

    const run = conversationRunOperation('ses_lead').responseSchema.parse(pageDto);
    expect(run.token_usage?.percent).toBe(65);
    expect(run.manual_compact_supported).toBe(true);

    expect(trackConversationsOperation('trk_1').path).toBe('/api/v1/tracks/trk_1/sessions');
    const acpConversations = trackConversationsOperation('trk_1').responseSchema.parse([
      { ...pageDto.session, id: 'ses_acp1', isLead: false, provider: 'acp:opencode', title: 'ACP Assistant' },
      { ...pageDto.session, id: 'ses_acp2', isLead: false, provider: 'acp:antigravity', title: 'AGY Assistant' },
    ]);
    expect(acpConversations.map((c) => c.provider)).toEqual(['opencode', 'antigravity']);

    const createClaudeConv = createTrackConversationOperation('trk_1', 'Investigate bug', 'idem-s1', 'claude', { model: null, reasoning_effort: null });
    expect(createClaudeConv.path).toBe('/api/v1/tracks/trk_1/sessions');
    expect(createClaudeConv.body).toMatchObject({ provider: 'claude' });

    const createOpencodeConv = createTrackConversationOperation('trk_1', 'ACP help', 'idem-s2', 'opencode', { model: null, reasoning_effort: null });
    expect(createOpencodeConv.body).toMatchObject({ provider: 'acp:opencode' });
    const createAgyConv = createTrackConversationOperation('trk_1', 'AGY help', 'idem-s3', 'antigravity', { model: null, reasoning_effort: null });
    expect(createAgyConv.body).toMatchObject({ provider: 'acp:antigravity' });

    const msgOp = sendPlannerInputOperation('ses_lead', 'Ship it', [], 'idem-m1', null, true);
    expect(msgOp).toMatchObject({
      method: 'POST',
      path: '/api/v1/sessions/ses_lead/messages',
      headers: { 'Idempotency-Key': 'idem-m1' },
    });
    expect(msgOp.body).toMatchObject({ content: 'Ship it', steerIfRunning: true });
    expect(interruptPlannerOperation('ses_lead').path).toBe('/api/v1/sessions/ses_lead/interrupt');
    expect(compactPlannerOperation('ses_lead').path).toBe('/api/v1/sessions/ses_lead/compact');

    expect(listTrackTerminalsOperation('trk_1').path).toBe('/api/v1/tracks/trk_1/terminals');
    expect(createTerminalCardOperation('trk_1', { theme: { fg: [0, 0, 0], bg: [0, 0, 0] } }, 'idem-term').path)
      .toBe('/api/v1/tracks/trk_1/terminals');
    expect(deleteTerminalOperation('trk_1', 'term_1').path).toBe('/api/v1/tracks/trk_1/terminals/term_1');

    expect(readTrackWorkspaceFileOperation('trk_1', 'src/index.ts').path)
      .toBe('/api/v1/tracks/trk_1/files?path=src%2Findex.ts');
    expect(listTrackWorkspaceDirectoryOperation('trk_1').path).toBe('/api/v1/tracks/trk_1/files');
    expect(trackVcsDiffOperation('trk_1').path).toBe('/api/v1/tracks/trk_1/vcs-diff');

    const delta = decodePergaEventEnvelope({
      eventId: null,
      areaId: 'area_1',
      trackId: 'trk_1',
      kind: 'session.text_delta',
      payload: { sessionId: 'ses_lead', turnId: 'turn_2', delta: 'Streaming...' },
      createdAtMs: 1500,
    });
    expect(delta).toMatchObject({ status: 'ready', kind: 'delta' });

    const persistent = decodeWireEvent({
      eventId: 42,
      areaId: 'area_1',
      trackId: 'trk_1',
      kind: 'report.updated',
      payload: { docRev: 5, summary: 'Updated' },
      createdAtMs: 1600,
    });
    expect(persistent).toMatchObject({ status: 'ready', value: { ev: 'report.updated' } });

    const trackUpdatedEvt = decodeWireEvent({
      eventId: 43,
      areaId: 'area_1',
      trackId: 'trk_1',
      kind: 'track.updated',
      payload: {
        id: 'trk_1',
        areaId: 'area_1',
        parentTrackId: null,
        parentAttemptId: null,
        title: 'Migrate FE',
        cwd: '/repo/.worktrees/trk_1',
        workspaceDriver: 'vcs-workspace',
        workspaceSpec: { driverId: 'vcs-workspace', logicalRoot: '/repo', mergedCwd: '/repo/.worktrees/trk_1' },
        leadSessionId: 'ses_lead',
        templateId: 'feature-dev',
        templateInputs: {},
        templateState: {},
        enabledPlugins: [],
        closedAtMs: null,
        pinnedAtMs: null,
        sortOrder: 1,
        createdAtMs: 1000,
        updatedAtMs: 2500,
      },
      createdAtMs: 2500,
    });
    expect(trackUpdatedEvt).toMatchObject({
      status: 'ready',
      value: {
        ev: 'track.updated',
        data: {
          workspace: {
            isolation: 'worktree',
            kind: 'attached',
            path: '/repo',
            frozen_at: null,
            worktree: '/repo/.worktrees/trk_1',
          },
        },
      },
    });

    const blocks = parsePergaMarkdownBlocks([
      '<!-- perga:b_1001 -->',
      '# Overview',
      '',
      '<!-- perga:b_1002 -->',
      '```perga-block task',
      '{"task_key":"verify","kind":"terminal","cmd":"npm test"}',
      '```',
    ].join('\n'));
    expect(blocks).toEqual([
      { id: 'b_1001', rev: 1, kind: 'prose', markdown: '# Overview', payload: { markdown: '# Overview' } },
      {
        id: 'b_1002',
        rev: 1,
        kind: 'task',
        markdown: '{"task_key":"verify","kind":"terminal","cmd":"npm test"}',
        payload: { task_key: 'verify', kind: 'terminal', cmd: 'npm test' },
      },
    ]);
  });
});
