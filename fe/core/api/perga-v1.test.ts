import { taskDtoSchema } from './perga-v1.js';
import hostPayloads from './fixtures/unified-host-payloads.json';
import { sessionContextUsage } from '../domain/session-context-usage.js';
import { describe, expect, it } from 'vitest';

import { bootstrapOperation, DEPLOYMENT_OWNER_USER_ID, loginOperation, whoamiOperation } from './auth.js';
import {
  bootstrapDtoSchema,
  decodePergaEventEnvelope,
  trackAggregateSnapshotDtoSchema,
  agentSessionDtoSchema,
  sessionCreationReceiptSchema,
  trackDtoSchema,
  trackDataPatchResultSchema,
} from './perga-v1.js';
import { decodeWireEvent } from './schemas.js';
import { decodeEventFrame } from '../events/protocol.js';
import { initialEventState, reduceEventFrame } from '../events/reducer.js';
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
  setConversationArchiveOperation,
  sendPlannerInputOperation,
  agentSessionDtoToConversation,
} from '../domain/conversation.js';
import { buildConversationTranscript, conversationItemsOperation, conversationRunOperation } from '../domain/conversation-display.js';

import {
  listDirectoryOperation,
  listTrackWorkspaceDirectoryOperation,
  readTrackWorkspaceFileOperation,
  trackVcsDiffOperation,
  trackWorkspaceRawFileUrl,
} from '../domain/fs.js';
import { selectedReadyTemplateId } from '../domain/mentions.js';
import { trackDtoToWire, trackActivityFromSnapshot } from '../domain/track.js';
import { plannerSwitchOperation } from '../domain/planner-switch.js';
import {
  readReportDocument,
  readTrackReportOperation,
  readTrackViewsOperation,
  trackTaskVerdictsOperation,
  updateTrackReportBlockOperation,
} from '../domain/report.js';
import {
  acceptTaskOperation,
  cancelTaskOperation,
  taskEvidence,
  rejectTaskOperation,
} from '../domain/task-recovery.js';
import { compilePergaTemplateInputs, parsePergaInputsSchema } from '../domain/template-input.js';
import {
  createTerminalCardOperation,
  createTrackOperation,
  deleteTerminalOperation,
  deleteTrackOperation,
  listTrackTerminalsOperation,
  trackOverlaysOperation,
  terminalDtoToCardWire,
  trackActivitiesFromOverlays,
  trackDetailOperation,
  tracksInAreaOperation,
  trackTemplatesOperation,
  updateTrackOperation,
} from '../domain/track.js';

describe('Perga /api/v1 protocol contracts', () => {
  it('preserves template presentation metadata without badge or metric count caps', () => {
    const header = {
      badges: Array.from({ length: 8 }, () => ({ label: 'Research', tone: 'accent' })),
      notice: { text: 'Pending', tone: 'info' },
      metrics: Array.from({ length: 9 }, () => ({ label: 'Sources', value: '7', spark: [1, 4, 7] })),
      rules: [{ label: 'Evidence', text: 'Verify sources' }],
    };
    const track = trackDtoSchema.parse({
      id: 'track', areaId: 'area', createdAtMs: 0, updatedAtMs: 0,
      templateTitle: 'Research', templateHeaderView: header,
    });
    expect(track.templateTitle).toBe('Research');
    expect(track.templateHeaderView).toEqual(header);
  });

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
    expect(operation.method).toBe('PATCH');
    expect(operation.path).toBe('/api/v1/sessions/session%2Fa');
    expect(operation.body).toEqual({ model: 'chosen', reasoningEffort: null });
    expect(operation.responseSchema.parse({
      id: 'session/a', trackId: 'track', provider: 'codex', model: 'chosen', contextUsage: null, createdAtMs: 1, updatedAtMs: 2,
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

  it('decodes business data revisions from the patch receipt and views endpoint', () => {
    const receipt = { ok: true, dataRev: 2, data: { records: [{ id: 'a' }] } };
    expect(trackDataPatchResultSchema.parse(receipt)).toEqual(receipt);
    expect(trackDataPatchResultSchema.safeParse({ ...receipt, dataRev: -1 }).success).toBe(false);

    const track = trackDtoSchema.parse({
      id: 'trk_1', areaId: 'area_1', createdAtMs: 0, updatedAtMs: 0,
      data: receipt.data, dataRev: receipt.dataRev,
    });
    expect(track.data).toEqual(receipt.data);
    expect(track.dataRev).toBe(2);
    expect(trackDtoSchema.safeParse({
      id: 'trk_1', areaId: 'area_1', createdAtMs: 0, updatedAtMs: 0, dataRev: -1,
    }).success).toBe(false);

    const operation = readTrackViewsOperation('trk_1');
    const views = operation.responseSchema.parse({ templateId: 'demo', dataRev: 2, views: { ledger: receipt.data } });
    expect(operation.path).toBe('/api/v1/tracks/trk_1/views');
    expect(views.dataRev).toBe(receipt.dataRev);
    expect(views.views.ledger).toEqual(receipt.data);

    const reportOp = readTrackReportOperation('trk_1');
    const report = reportOp.responseSchema.parse({
      docRev: 1, dataRev: 2, summary: 'Ledger', renderedMarkdown: '# Ledger', blocks: [], tasks: [], diagnostics: [],
    });
    expect(report.dataRev).toBe(2);
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
          sessionId: 'ses_worker1',
          runCount: 1,
          createdAtMs: 1200,
          updatedAtMs: 2400,
        },
      ],
      sessions: [
        {
          id: 'ses_lead',
          trackId: 'trk_1',
          isLead: true,
          title: 'Lead',
          provider: 'codex',
          model: 'gpt-5',
          nativeThreadId: 'thr_1',
          epoch: 2,
          status: 'running',
          contextUsage: { usedTokens: 107_520, windowTokens: 256_000, observedAtMs: 2400, ratio: 0.42 },
          archivedAtMs: null,
          createdAtMs: 1000,
          updatedAtMs: 2500,
        },
      ],
      openAsks: [
        {
          id: 'ask_abc123',
          delivery: 'wake', sessionId: null, turnId: null,
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
    expect(trackDtoToWire(detail.track).workspace).toEqual({
      isolation: 'worktree',
      kind: 'attached',
      path: '/repo',
      frozen_at: null,
      worktree: '/repo/.worktrees/trk_1',
    });
    expect(detail.report.docRev).toBe(4);
    expect(detail.report.blocks.map(block => [block.id, block.rev])).toEqual([['b_prose1', 2], ['b_task1', 1]]);
    expect(detail.report.blocks.map(block => block.kind)).toEqual(['prose', 'task']);
    const acpDetail = detailOp.responseSchema.parse({ ...snapshotRaw,
      sessions: [{ ...snapshotRaw.sessions[0], provider: 'acp:antigravity' }] });
    expect(agentSessionDtoToConversation(acpDetail.sessions[0]))
      .toMatchObject({ kind: 'agent', isLead: true, provider: 'antigravity' });

    const activity = trackActivityFromSnapshot(detail);
    expect(activity.working).toBe(true);
    expect(activity.attention).toBe('input');
    const asks = openAsksOf(activity.attentionItems);
    expect(asks[0]).toMatchObject({ askId: 'ask_abc123', delivery: 'wake' });
    const holdAsk = { ...snapshotRaw.openAsks[0], delivery: 'hold', sessionId: 'lead_1', turnId: 'turn_1' };
    const heldDetail = detailOp.responseSchema.parse({ ...snapshotRaw, openAsks: [holdAsk] });
    const heldActivity = trackActivityFromSnapshot(heldDetail);
    expect(openAsksOf(heldActivity.attentionItems)[0]).toMatchObject({ askId: 'ask_abc123', delivery: 'hold' });
    const holdEvent = { eventId: 44, kind: 'ask.changed', areaId: 'area_1', trackId: 'trk_1', payload: holdAsk, createdAtMs: 2300 };
    expect(decodeWireEvent(holdEvent)).toMatchObject({ status: 'ready', value: {
      ev: 'ask.changed', data: { ask: holdAsk },
    } });
    // Missing public identity is a contract error, never a reason to infer from text.
    const { delivery: omittedDelivery, ...anonymousLegacyAsk } = holdAsk;
    expect(omittedDelivery).toBe('hold');
    expect(decodeWireEvent({ ...holdEvent, payload: anonymousLegacyAsk }).status).toBe('failed');

    const answerOp = answerAskOperation('trk_1', 'ask_abc123', [{ option: 0 }], asks[0]?.questions);
    expect(answerOp).toMatchObject({
      method: 'POST',
      path: '/api/v1/sessions/trk_1/interactions/ask_abc123/respond',
      body: { answers: [{ option: 0, value: 'Approve' }] },
    });

    const createOp = createTrackOperation({
      area_id: 'area_1',
      planner_provider: 'codex',
      title: 'New Track',
      cwd: '/repo',
      isolation: 'worktree',
      theme: { fg: [0, 0, 0], bg: [255, 255, 255] },
    }, 'idem-t1');
    expect(createOp.path).toBe('/api/v1/tracks');
    expect(createOp.body).toMatchObject({
      areaId: 'area_1',
      provider: 'codex',
      workspaceDriver: 'vcs-workspace',
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

    const switchOp = plannerSwitchOperation('trk_1', 'ses_sub1');
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
  });

  it('maps Binary Sessions (Lead + Subagent), Terminals, Files, VCS Diff, and WS envelopes', () => {
    const pageDto = {
      items: [
        { id: 1, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'user', payload: { v: 1, content: 'Hello Perga', triggerKind: 'user' }, createdAtMs: 1000 },
        { id: 2, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'tool_call', payload: { v: 1, callId: 'c1', name: 'report_read', args: {} }, createdAtMs: 1100 },
        { id: 3, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'tool_result', payload: { v: 1, callId: 'c1', output: 'ok', isError: false }, createdAtMs: 1200 },
        { id: 4, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'thinking', payload: { v: 1, text: 'Planning...' }, createdAtMs: 1250 },
        { id: 5, sessionId: 'ses_lead', turnId: 'turn_1', kind: 'assistant_text', payload: { v: 1, text: 'Report inspected.' }, createdAtMs: 1300 },
      ],
      activeTurn: null,
      state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null,
      session: {
        id: 'ses_lead',
        trackId: 'trk_1',
        isLead: true,
        title: 'Lead',
        provider: 'codex',
        model: 'gpt-5',
        nativeThreadId: 'thr_1',
        epoch: 2,
        status: 'idle',
        contextUsage: { usedTokens: 166_400, windowTokens: 256_000, observedAtMs: 1250, ratio: 0.65 },
        archivedAtMs: null,
        createdAtMs: 900,
        updatedAtMs: 1300,
      },
    };

    const itemsOp = conversationItemsOperation('ses_lead', null);
    expect(itemsOp.path).toBe('/api/v1/sessions/ses_lead/items?afterId=0&limit=60&latest=true');
    const page = itemsOp.responseSchema.parse(pageDto);
    const transcript = buildConversationTranscript(page.items, 'local-owner');
    expect(transcript.map((e) => (e.author === 'activity' ? `${e.author}:${e.verb}` : `${e.author}:${e.text}`))).toEqual([
      'you:Hello Perga',
      'activity:Read report',
      'activity:Thought',
      'agent:Report inspected.',
    ]);

    const run = conversationRunOperation('ses_lead').responseSchema.parse(pageDto);
    expect(sessionContextUsage(run.session)).toEqual({ used_tokens: 166_400, context_window: 256_000, percent: 65, at_ms: 1250 });
    expect(run.taskBinding == null).toBe(true);

    const acpConversations = [
      { ...pageDto.session, id: 'ses_acp1', isLead: false, provider: 'acp:opencode', title: 'ACP Assistant' },
      { ...pageDto.session, id: 'ses_acp2', isLead: false, provider: 'acp:antigravity', title: 'AGY Assistant' },
    ].map(session => agentSessionDtoToConversation(agentSessionDtoSchema.parse(session)));
    expect(acpConversations.map((c) => c.provider)).toEqual(['opencode', 'antigravity']);

    const createClaudeConv = createTrackConversationOperation('trk_1', 'Investigate bug', 'idem-s1', 'claude', { model: null, reasoning_effort: null });
    expect(createClaudeConv.path).toBe('/api/v1/tracks/trk_1/sessions');
    expect(createClaudeConv.body).toMatchObject({ provider: 'claude' });

    const createOpencodeConv = createTrackConversationOperation('trk_1', 'ACP help', 'idem-s2', 'opencode', { model: null, reasoning_effort: null });
    expect(createOpencodeConv.body).toMatchObject({ provider: 'acp:opencode' });
    const createAgyConv = createTrackConversationOperation('trk_1', 'AGY help', 'idem-s3', 'antigravity', { model: null, reasoning_effort: null });
    expect(createAgyConv.body).toMatchObject({ provider: 'acp:antigravity' });

    const msgOp = sendPlannerInputOperation('ses_lead', 'Ship it', [], 'idem-m1', true);
    expect(msgOp).toMatchObject({
      method: 'POST',
      path: '/api/v1/sessions/ses_lead/messages',
      headers: { 'Idempotency-Key': 'idem-m1' },
    });
    expect(msgOp.body).toMatchObject({ content: 'Ship it', mode: 'steer' });
    expect(interruptPlannerOperation('ses_lead').path).toBe('/api/v1/sessions/ses_lead/control');
    expect(compactPlannerOperation('ses_lead').path).toBe('/api/v1/sessions/ses_lead/control');

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
  });

  it('covers user-journey audit fixes across CHAT, CARD, and SHELL contracts', () => {
    // SHELL-02: GET loginOperation never includes a request body
    expect(loginOperation({ username: 'u', password: 'p' })).not.toHaveProperty('body');

    // CHAT-03, CHAT-04 & CHAT-05: 10-minute timeout, SessionInputReceipt -> entry_id, and null-entryId outbox retirement
    const sendOp = sendPlannerInputOperation('ses_lead', 'Ship Perga', [], 'idem-1');
    expect(sendOp.timeoutMs).toBe(600_000);
    expect(compactPlannerOperation('ses_lead').timeoutMs).toBe(600_000);
    expect(sendOp.responseSchema.parse({
      messageId: 'inp_77', delivery: 'accepted',
      turnId: 'turn_9',
      sessionId: 'ses_lead',
      deliveryState: 'accepted',
      turnStatus: 'queued',
      resolvedAtMs: null,
    })).toEqual({ card_id: 'ses_lead', worker_session_id: 'ses_lead', entry_id: 'inp_77', delivery: 'accepted', turnId: 'turn_9', queued: false });



    // CHAT-06, CHAT-07 & CHAT-11: beforeId pagination, turn_outcome error projection, mcpToolCall content, permission_mode: null
    const cursorOp = conversationItemsOperation('ses_lead', {
      contract: 'conversation_v1',
      card_id: 'ses_lead',
      source: 'presentation',
      position: 50,
    });
    expect(cursorOp.path).toContain('&beforeId=50');
    const errorPage = conversationItemsOperation('ses_lead', null).responseSchema.parse({
      items: [
        { id: 10, sessionId: 'ses_lead', turnId: 't_err', kind: 'user', payload: { content: 'Run tool' }, createdAtMs: 100 },
        { id: 11, sessionId: 'ses_lead', turnId: 't_err', kind: 'tool_call', payload: { callId: 'c_9', name: 'custom_lookup', args: { query: 'perga' } }, createdAtMs: 110 },
        { id: 12, sessionId: 'ses_lead', turnId: 't_err', kind: 'tool_result', payload: { callId: 'c_9', output: { found: 3 }, isError: false }, createdAtMs: 120 },
        { id: 13, sessionId: 'ses_lead', turnId: 't_err', kind: 'turn_outcome', payload: { status: 'failed', error: 'Model quota exceeded' }, createdAtMs: 130 },
      ],
      hasMoreBefore: true,
      activeTurn: null,
      state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null,
      session: {
        id: 'ses_lead', trackId: 'trk_1', isLead: true,
        title: 'Lead', provider: 'codex', model: 'gpt-5', nativeThreadId: 'thr_1', epoch: 1,
        status: 'idle', contextUsage: null, archivedAtMs: null, createdAtMs: 90, updatedAtMs: 130,
      },
    });
    expect(errorPage.next_cursor?.position).toBe(10);
    const errorTranscript = buildConversationTranscript(errorPage.items, DEPLOYMENT_OWNER_USER_ID);
    expect(errorTranscript[0]).toMatchObject({ author: 'you', text: 'Run tool' });
    expect(errorTranscript[1]).toMatchObject({ author: 'activity' });
    if (errorTranscript[1]?.author === 'activity') {
      expect(errorTranscript[1].content).toContain('"query": "perga"');
      expect(errorTranscript[1].content).toContain('"found": 3');
    }
    expect(errorTranscript[2]).toMatchObject({
      author: 'turn',
      status: 'failed',
      message: 'Model quota exceeded',
      text: 'Model quota exceeded',
    });

    const run = conversationRunOperation('ses_lead').responseSchema.parse({
      items: [],
      activeTurn: null,
      state: 'idle', readonlyReason: null, canReopen: false, workerPaused: false, lastTurn: null, unknownDetail: null,
      session: {
        id: 'ses_lead', trackId: 'trk_1', isLead: true,
        title: 'Lead', provider: 'codex', model: 'gpt-5', nativeThreadId: 'thr_1', epoch: 1,
        status: 'idle', contextUsage: null, archivedAtMs: null, createdAtMs: 90, updatedAtMs: 130,
      },
    });
    expect(run.session.permissionMode ?? null).toBeNull();

    const setModelOp = setPlannerModelOperation('ses_lead', { model: null, reasoning_effort: null });
    expect(setModelOp).toMatchObject({
      method: 'PATCH',
      path: '/api/v1/sessions/ses_lead',
      body: { model: null, reasoningEffort: null },
    });
    const archiveOp = setConversationArchiveOperation('trk_1', 'ses_sub1', true);
    expect(archiveOp).toMatchObject({
      method: 'PATCH',
      path: '/api/v1/sessions/ses_sub1',
      body: { archived: true },
    });
    const cardScopedCatalog = modelCatalogOperation({ kind: 'card', cardId: 'ses_lead', provider: 'claude' })
      .responseSchema.parse(sampleBootstrap);
    expect(cardScopedCatalog.source).toBe('unavailable');
    expect(cardScopedCatalog.models).toEqual([]);

    // CHAT-09: refresh the snapshot before committing its cursor.
    const decodedSnap = decodeEventFrame({ kind: '_snapshot_required', tipId: 99 });
    expect(decodedSnap).toEqual({ status: 'ready', frame: { type: 'snapshot-required', tipId: 99 } });
    if (decodedSnap.status === 'ready') {
      const reduced = reduceEventFrame(initialEventState(1, 5), decodedSnap.frame);
      expect(reduced.state.cursor).toBe(5);
      expect(reduced.effects).toEqual([{ type: 'refresh-snapshot', tipId: 99 }]);
    }

    // CARD-01, CARD-02, CARD-03, CARD-04, CARD-05 & SHELL-03
    const termCard = terminalDtoToCardWire({
      id: 'term_1',
      trackId: 'trk_1',
      sessionId: null,
      title: 'Shell',
      cwd: '/repo',
      program: '/bin/bash',
      status: 'running',
      exitCode: null,
      createdAtMs: 1000,
      exitedAtMs: null,
    });
    expect(termCard.payload).toMatchObject({
      terminal_presentation: { source: 'unrecorded', in_workbench: true },
    });

    const hostListOp = listDirectoryOperation('/srv', { perga: true });
    expect(hostListOp.path).toBe('/api/v1/fs/listdir?path=%2Fsrv');
    expect(hostListOp.responseSchema.parse({
      path: '/srv',
      parent: '/',
      entries: [{ name: 'repo', isDir: true, isSymlink: false }],
    })).toEqual({
      path: '/srv',
      parent: '/',
      entries: [{ name: 'repo', is_dir: true, is_symlink: false }],
    });
    expect(trackWorkspaceRawFileUrl('trk_1', 'assets/logo.png'))
      .toBe('/api/v1/tracks/trk_1/files/raw?path=assets%2Flogo.png');

    const pergaFields = parsePergaInputsSchema({
      baseBranch: { type: 'string', default: 'main', label: 'Base Branch' },
      horizonMonths: { type: 'number', default: 12, label: 'Horizon' },
      requirePrReview: { type: 'boolean', default: true, label: 'Require PR Review' },
      riskLevel: { type: 'enum', values: ['low', 'medium', 'high'], default: 'medium', label: 'Risk Level' },
    });
    expect(pergaFields).toHaveLength(4);
    expect(compilePergaTemplateInputs(pergaFields, { horizonMonths: '24', riskLevel: 'high' })).toEqual({
      valid: true,
      input: { baseBranch: 'main', horizonMonths: 24, requirePrReview: true, riskLevel: 'high' },
      errors: {},
    });
    expect(selectedReadyTemplateId('Use @`template:blank`', [{
      id: 'blank', title: 'Blank', description: null, availability: { status: 'ready' }, input_schema: {}, tasks: [],
    }])).toBe('blank');

    const evidence = taskEvidence(taskDtoSchema.parse({
      id: 'task', trackId: 'trk_1', blockId: 'block', taskKey: 'build-fe', kind: 'agent', status: 'failed',
      sessionId: 'ses_worker', runCount: 2, createdAtMs: 1000, updatedAtMs: 1420,
      workerResult: { summary: 'Partial progress' }, gateVerdict: { passed: false,
        steps: [{ name: 'test', cmd: 'npm test', exitCode: 1, passed: false, durationMs: 420, stdout: '1 failing', stderr: 'AssertionError', outputTail: '' }] },
    }));
    expect(evidence.gateSteps[0]?.output).toContain('AssertionError');
    expect(evidence.workerResult).toContain('Partial progress');
    expect(cancelTaskOperation('trk_1', 'build-fe').path).toBe('/api/v1/tracks/trk_1/tasks/build-fe/cancel');
    expect(acceptTaskOperation('trk_1', 'build-fe').path).toBe('/api/v1/tracks/trk_1/tasks/build-fe/accept');
    expect(rejectTaskOperation('trk_1', 'build-fe').path).toBe('/api/v1/tracks/trk_1/tasks/build-fe/reject');

    const agyReport = readTrackReportOperation('trk_1').responseSchema.parse({
      docRev: 2,
      summary: 'AGY task report',
      renderedMarkdown: '',
      blocks: [
        {
          id: 'b_agy',
          kind: 'task',
          markdown: '',
          payload: {
            taskKey: 'audit-agy',
            kind: 'agent',
            provider: 'acp:antigravity',
            goalOrCmd: 'Run ACP verification',
            dependsOn: ['prep'],
            ready: true,
          },
        },
        {
          id: 'b_oc',
          kind: 'task',
          markdown: '',
          payload: {
            taskKey: 'audit-oc',
            kind: 'acp:opencode',
            goal_or_cmd: 'Inspect workspace',
            ready: false,
          },
        },
      ],
      tasks: [],
      diagnostics: [],
    });
    const parsedAgyReport = readReportDocument({ ...agyReport, leadingComment: '' });
    expect(parsedAgyReport?.blocks).toMatchObject([
      { id: 'b_agy', kind: 'task', payload: { key: 'audit-agy', kind: 'antigravity', goal: 'Run ACP verification', depends_on: ['prep'], ready: true } },
      { id: 'b_oc', kind: 'task', payload: { key: 'audit-oc', kind: 'opencode', goal: 'Inspect workspace', ready: false } },
    ]);

    const overlaysOp = trackOverlaysOperation();
    expect(overlaysOp.path).toBe('/api/v1/overlays?entity_kind=track&includeClosed=true');
    const overlays = overlaysOp.responseSchema.parse({
      tracks: [{
        trackId: 'trk_1',
        areaId: 'area_1',
        isWorking: true,
        openAskCount: 1,
        hasFailedTask: false,
        unreadMailCount: 0,
        updatedAtMs: 5000,
      }],
    });
    const trackAct = trackActivitiesFromOverlays(overlays).get('trk_1');
    expect(trackAct?.working).toBe(true);
    expect(trackAct?.attention).toBe('input');
  });
});

// Captured from the C# DTOs using PergaJsonDefaults.ApiOptions (Never omits nulls).
it('decodes the Host creation receipt without a legacy status URL', () => {
  expect(sessionCreationReceiptSchema.parse(hostPayloads.creation)).toEqual(hostPayloads.creation);
  const operation = createTrackConversationOperation('track', 'Hello', 'create', 'codex', {
    model: null, reasoning_effort: null,
  });
  expect(operation.responseSchema.parse(hostPayloads.creation)).toMatchObject({
    session: hostPayloads.creation.session, sessionInput: hostPayloads.creation.firstInput,
  });
  expect(sessionCreationReceiptSchema.parse(hostPayloads.emptyCreation)).toEqual(hostPayloads.emptyCreation);
});


it('decodes task ownership and latest evidence from the C# task fixture', () => {
  const task = taskDtoSchema.parse(hostPayloads.task);
  expect(task).toMatchObject({ sessionId: 'session', runCount: 2, workerResult: { summary: 'Latest result' } });
  expect(taskEvidence(task)).toMatchObject({ gatePassed: false, gateSteps: [{ output: 'Check failed' }] });
  expect(task).not.toHaveProperty('latestAttemptId');
});
