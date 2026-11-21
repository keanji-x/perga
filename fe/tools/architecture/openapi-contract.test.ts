import { z } from 'zod';
import hostPayloads from '../../core/api/fixtures/unified-host-payloads.json';
import { readFileSync } from 'node:fs';
import { systemUpgradeReceiptSchema } from '../../core/api/perga-v1.ts';
import { compileTemplateInputs } from '../../core/domain/template-input.ts';
import { describe, expect, it } from 'vitest';

type JsonObject = Record<string, unknown>;

const METHODS = new Set(['delete', 'get', 'head', 'options', 'patch', 'post', 'put', 'trace']);
const PATH_ITEM_FIELDS = new Set([...METHODS, 'summary', 'description', 'servers', 'parameters']);
/* Response schemas with no `wire.ts` counterpart: `gen:api` exports from calm-types only, so a DTO
 * declared in a calm-server route module is typed by a hand-written zod schema in `core/domain/**` instead. */
const RESPONSE_WIRE_EXCEPTIONS = new Set([
  'ApprovalDto', 'ErrorBody', 'GetPlannerRunResponse', 'GitDiffResponse', 'GitStatusResponse',
  // Route-owned negotiated adapters, decoded by conversation-display.ts and queries.ts.
  // Their exact composition is pinned below; component wire models remain generated.
  'ConversationItemsReadResponse', 'HarnessLiveReadResponse', 'PlannerRunReadResponse',
  'InterruptPlannerCardResponse', 'ListdirResponse', 'ModelsResponse', 'PluginDetail', 'PluginListItem',
  'PlannerInputMutationResponse', 'PlannerInputStaleBody',
  'PlannerSteerResponse', 'PlannerSteerConflictBody',
  // Route-owned Planner switch DTOs; this REST-only slice has no frontend mutation consumer yet.
  // Their response shapes are exercised through the authenticated router's planner_switch_http_ tests.
  'PlannerSwitchResponse', 'PlannerSwitchRefusalBody',
  'ReadFileResponse', 'ReportBlockWriteResponse',
  'ResetPlannerCardResponse', 'SendPlannerInputResponse',
  'SetPlannerModelResponse',
  // Route-owned DTO decoded by authenticationRetryResponseSchema in core/domain/agent-providers.ts.
  'AuthenticationRetryResponse',
  // Route-owned DTO decoded by compactPlannerOperation in core/domain/conversation.ts.
  'CompactPlannerResponse',
  'SettingsBag', 'Terminal',
  'ThreadCardResolution', 'TodayLaunchpad', 'TodayLaunchpadReportReset', 'TodayLaunchpadResolved',
  'TodaySummaryStarted',
  'VersionInfo',
  'ViewCatalogEntry', 'TrackBacklinksResponse', 'TrackDetail', 'TrackFsContent', 'TrackFsEntry',
  'TrackReportReadResponse', 'TrackTemplate',
  // Read-only server DTO, decoded by core/domain/template.ts; its shape is pinned below.
  'TrackTemplateDetail',
  'TemplatePluginGuide',
  // Login answer, decoded by `sessionIdentitySchema` in core/api/auth.ts.
  'WhoamiBody',
  'ReportSeriesResolved', 'ReportSeriesRevConflict', 'TrackPreviews',
]);

function object(value: unknown): value is JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function array(value: unknown): unknown[] {
  return Array.isArray(value) ? value as unknown[] : [];
}

function resolveRef(document: JsonObject, ref: string): unknown {
  if (!ref.startsWith('#/')) throw new Error(`external reference: ${ref}`);
  return ref.slice(2).split('/').reduce<unknown>((value, encoded) => {
    const key = encoded.replaceAll('~1', '/').replaceAll('~0', '~');
    if (!object(value) || !(key in value)) throw new Error(`unresolved reference: ${ref}`);
    return value[key];
  }, document);
}

function parsePath(path: string): { names: string[]; shape: string } {
  if (!path.startsWith('/') || path.includes('?') || path.includes('#')) throw new Error(`invalid path: ${path}`);
  const names: string[] = [];
  let shape = '';
  let literal = '';
  for (let cursor = 0; cursor < path.length;) {
    const character = path[cursor];
    if (character === '}') throw new Error(`unmatched }: ${path}`);
    if (character !== '{') {
      literal += character;
      cursor += 1;
      continue;
    }
    if (shape.endsWith('{}') && literal === '') throw new Error(`adjacent parameters: ${path}`);
    shape += literal;
    literal = '';
    const end = path.indexOf('}', cursor + 1);
    if (end < 0) throw new Error(`unclosed {: ${path}`);
    const name = path.slice(cursor + 1, end);
    if (name === '' || name.includes('{') || names.includes(name)) throw new Error(`invalid parameter: ${path}`);
    names.push(name);
    shape += '{}';
    cursor = end + 1;
  }
  return { names, shape: shape + literal };
}

function resolveParameter(document: JsonObject, value: unknown): JsonObject {
  if (!object(value)) throw new Error('parameter must be an object');
  if (typeof value.$ref !== 'string') return value;
  const resolved = resolveRef(document, value.$ref);
  if (!object(resolved)) throw new Error(`parameter reference is not an object: ${value.$ref}`);
  return resolved;
}

function visitRefs(
  document: JsonObject,
  value: unknown,
  visited = new Set<string>(),
): void {
  if (Array.isArray(value)) {
    value.forEach((item) => visitRefs(document, item, visited));
    return;
  }
  if (!object(value)) return;
  if (typeof value.$ref === 'string') {
    const ref = value.$ref;
    const resolved = resolveRef(document, ref);
    if (!visited.has(ref)) {
      visited.add(ref);
      visitRefs(document, resolved, visited);
    }
  }
  Object.entries(value).forEach(([key, item]) => {
    if (key !== '$ref') visitRefs(document, item, visited);
  });
}

function responseRootRefs(document: JsonObject, responses: JsonObject): string[] {
  const refs: string[] = [];
  for (const rawResponse of Object.values(responses)) {
    const response = object(rawResponse) && typeof rawResponse.$ref === 'string'
      ? resolveRef(document, rawResponse.$ref)
      : rawResponse;
    if (!object(response) || !object(response.content)) continue;
    for (const media of Object.values(response.content)) {
      if (!object(media) || !object(media.schema) || typeof media.schema.$ref !== 'string') continue;
      if (media.schema.$ref.startsWith('#/components/schemas/')) refs.push(media.schema.$ref.slice(21));
    }
  }
  return refs;
}

function validateDocument(document: JsonObject, wireSource: string): void {
  if (!object(document.paths)) throw new Error('paths must be an object');
  const routeShapes = new Set<string>();
  const responseSchemaRefs = new Set<string>();
  for (const [path, rawPathItem] of Object.entries(document.paths)) {
    if (!object(rawPathItem)) throw new Error(`path item must be an object: ${path}`);
    for (const key of Object.keys(rawPathItem)) {
      if (!PATH_ITEM_FIELDS.has(key) && !key.startsWith('x-')) throw new Error(`unknown path item field: ${key}`);
    }
    const template = parsePath(path);
    if (routeShapes.has(template.shape)) throw new Error(`ambiguous route shape: ${path}`);
    routeShapes.add(template.shape);
    const sharedParameters = array(rawPathItem.parameters);
    for (const [method, rawOperation] of Object.entries(rawPathItem)) {
      if (!METHODS.has(method)) continue;
      if (!object(rawOperation)) throw new Error(`operation must be an object: ${method} ${path}`);
      const declared = [...sharedParameters, ...array(rawOperation.parameters)]
        .map((value) => resolveParameter(document, value))
        .filter((value) => value.in === 'path' && typeof value.name === 'string')
        .map((value) => value.name as string);
      expect(new Set(declared)).toEqual(new Set(template.names));
      if (!object(rawOperation.responses) || Object.keys(rawOperation.responses).length === 0) {
        throw new Error(`operation has no responses: ${method} ${path}`);
      }
      responseRootRefs(document, rawOperation.responses).forEach((name) => responseSchemaRefs.add(name));
      visitRefs(document, rawOperation.responses);
    }
  }
  const wireTypes = new Set(Array.from(
    wireSource.matchAll(/^export\s+(?:type|interface)\s+([A-Za-z_$][\w$]*)/gm),
    (match) => match[1],
  ));
  const missing = [...responseSchemaRefs]
    .filter((name) => !wireTypes.has(name) && !RESPONSE_WIRE_EXCEPTIONS.has(name))
    .sort();
  if (missing.length > 0) throw new Error(`response schema wire types missing: ${missing.join(', ')}`);
}

describe('generated OpenAPI integrity', () => {
  it('keeps every operation unambiguous, referenced, complete, and wire-covered', () => {
    const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
    const wire = readFileSync(new URL('../../core/api/generated/wire.ts', import.meta.url), 'utf8');
    validateDocument(document, wire);
  });

  it('rejects malformed templates and nested dangling references', () => {
    for (const path of ['/a/{id}/{id}', '/a/{id', '/a/id}', '/a/{a}{b}', '/a?q=1', '/a#x']) {
      expect(() => parsePath(path)).toThrow();
    }
    const document = {
      paths: {
        '/a': { get: { responses: { 200: { $ref: '#/components/responses/A' } } } },
      },
      components: {
        responses: { A: { content: { 'application/json': { schema: { $ref: '#/components/schemas/Missing' } } } } },
        schemas: {},
      },
    };
    expect(() => validateDocument(document, '')).toThrow('unresolved reference');
  });

  it('rejects an operation without responses', () => {
    expect(() => validateDocument({ paths: { '/a': { get: {} } } }, ''))
      .toThrow('operation has no responses');
  });

  it('rejects a top-level response schema without a wire type', () => {
    const document = {
      paths: {
        '/a': { get: { responses: { 200: { content: { 'application/json': {
          schema: { $ref: '#/components/schemas/MissingWire' },
        } } } } } },
      },
      components: { schemas: { MissingWire: { type: 'object' } } },
    };
    expect(() => validateDocument(document, '')).toThrow('response schema wire types missing: MissingWire');
  });
});

const NEGOTIATED_RESPONSE_ADAPTERS = Object.freeze([
  'ConversationItemsReadResponse', 'HarnessLiveReadResponse', 'PlannerRunReadResponse',
] as const);

it('pins negotiated route adapters to their actual generated component schemas', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: Record<string, JsonObject> };
  };
  const schemas = document.components.schemas;
  expect(schemas.ConversationItemsReadResponse).toEqual({ oneOf: [
    { type: 'array', items: { $ref: '#/components/schemas/HarnessItem' } },
    { $ref: '#/components/schemas/ConversationPage' },
  ] });
  expect(schemas.HarnessLiveReadResponse).toEqual({ oneOf: [
    { $ref: '#/components/schemas/HarnessLiveReplies' },
    { $ref: '#/components/schemas/ConversationLiveReplies' },
  ] });
  // The negotiated run extends the historical object, so a valid answer can
  // match both shapes. oneOf would reject that overlap.
  expect(Object.keys(schemas.PlannerRunReadResponse)).toEqual(['anyOf']);
  const branches = array(schemas.PlannerRunReadResponse.anyOf);
  expect(branches).toHaveLength(2);
  expect(branches[0]).toEqual(schemas.GetPlannerRunResponse);
  if (!object(branches[1])) throw new Error('Missing negotiated run composition');
  const composition = array(branches[1].allOf);
  expect(composition).toHaveLength(2);
  expect(composition[0]).toEqual({ $ref: '#/components/schemas/GetPlannerRunResponse' });
  if (!object(composition[1]) || !object(composition[1].properties)) throw new Error('Missing run metadata');
  const fields = ['active_input', 'history_replacement_supported', 'input_contract',
    'native_turn_id', 'pending_inputs', 'runtime_state', 'terminal_outcome', 'terminal_timeout'];
  expect(Object.keys(composition[1].properties).sort()).toEqual(fields);
  expect(array(composition[1].required).toSorted()).toEqual(fields);
  expect(composition[1].properties.history_replacement_supported).toMatchObject({ type: 'boolean' });
  expect(composition[1].properties.input_contract).toEqual({ $ref: '#/components/schemas/ConversationInputContract' });
  expect(composition[1].properties.pending_inputs).toEqual({ type: 'array',
    items: { $ref: '#/components/schemas/ConversationPendingInput' } });
});

it.each(NEGOTIATED_RESPONSE_ADAPTERS)('does not exempt an undeclared adapter resembling %s', (name) => {
  const undeclared = `Undeclared${name}`;
  const document = {
    paths: {
      '/a': { get: { responses: { 200: { content: { 'application/json': {
        schema: { $ref: `#/components/schemas/${undeclared}` },
      } } } } } },
    },
    components: { schemas: { [undeclared]: { type: 'object' } } },
  };
  expect(() => validateDocument(document, '')).toThrow(`response schema wire types missing: ${undeclared}`);
});

it('pins the server DTO outside the generated wire type roster', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: { TrackTemplateDetail: { properties: Record<string, unknown>; required: string[] } } };
  };
  const schema = document.components.schemas.TrackTemplateDetail;
  expect(Object.keys(schema.properties).sort()).toEqual(['body', 'description', 'id', 'instructions', 'title']);
  expect(schema.required.sort()).toEqual(['body', 'id', 'title']);
});

it('publishes the nullable session measurement through the v1 response', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: Record<string, { properties: Record<string, unknown>; required: string[] }> };
  };
  const { AgentSessionDto, SessionContextUsage } = document.components.schemas;
  expect(AgentSessionDto.required).toContain('contextUsage');
  expect(AgentSessionDto.properties.contextUsage).toEqual({
    anyOf: [{ $ref: '#/components/schemas/SessionContextUsage' }, { type: 'null' }],
  });
  expect(AgentSessionDto.properties).not.toHaveProperty('contextUsageInvalidated');
  expect(SessionContextUsage.required).toEqual(['usedTokens', 'windowTokens', 'observedAtMs', 'ratio']);
});

it('compact capability is required in the generated Planner run contract', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: { GetPlannerRunResponse: { properties: Record<string, unknown>; required: string[] } } };
  };
  const schema = document.components.schemas.GetPlannerRunResponse;
  expect(schema.properties.manual_compact_supported).toMatchObject({ type: 'boolean' });
  expect(schema.required).toContain('manual_compact_supported');
  expect(schema.required).toContain('compact_outcome');
});


it('declares all Perga /api/v1 REST endpoints in openapi.json', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    paths: Record<string, Record<string, unknown>>;
  };
  const expectedPaths = [
    '/api/v1/bootstrap',
    '/api/v1/templates',
    '/api/v1/areas',
    '/api/v1/areas/{areaId}',
    '/api/v1/areas/{areaId}/tracks',
    '/api/v1/tracks',
    '/api/v1/tracks/{trackId}',
    '/api/v1/tracks/{trackId}/files',
    '/api/v1/tracks/{trackId}/vcs-diff',
    '/api/v1/tracks/{trackId}/report',
    '/api/v1/tracks/{trackId}/report/blocks/{blockId}',
    '/api/v1/tracks/{trackId}/tasks/{taskKey}/attempts',
    '/api/v1/tracks/{trackId}/sessions',
    '/api/v1/sessions/{sessionId}/items',
    '/api/v1/sessions/{sessionId}/messages',
    '/api/v1/sessions/{sessionId}/control',
    '/api/v1/sessions/{sessionId}/messages/{messageId}',
    '/api/v1/sessions/{sessionId}/interactions/{interactionId}/respond',
    '/api/v1/tracks/{trackId}/terminals',
    '/api/v1/tracks/{trackId}/terminals/{termId}',
  ];
  for (const path of expectedPaths) {
    expect(document.paths[path], `missing ${path}`).toBeDefined();
  }
  expect(document.paths['/api/calendar/tasks']).toBeUndefined();
});

it('compiles shipped Perga .perga.js template input schemas cleanly', () => {
  const devSource = readFileSync(new URL('../../../templates/dev.perga.js', import.meta.url), 'utf8');
  expect(devSource).toContain('id: "dev"');
  const devInputsSchema = {
    type: 'object',
    properties: {
      baseBranch: { type: 'string', default: 'main', title: 'Upstream base branch' },
      testCmd: { type: 'string', default: 'dotnet test', title: 'Default verification command' },
      requirePrReview: { type: 'boolean', default: true, title: 'Require human confirmation before merging PR' },
      riskLevel: { type: 'string', enum: ['low', 'medium', 'high'], default: 'medium', title: 'Change risk profile' },
    },
  };
  const formHeader = `<!-- neige:input-form ${JSON.stringify({
    version: 1,
    groups: [{
      title: 'Development Settings',
      description: 'Engineering inputs',
      fields: [
        { kind: 'text', key: 'baseBranch', label: 'Upstream base branch', default: 'main', required: false, placeholder: 'main', help: '', format: null },
        { kind: 'text', key: 'testCmd', label: 'Default verification command', default: 'dotnet test', required: false, placeholder: 'dotnet test', help: '', format: null },
        { kind: 'toggle', key: 'requirePrReview', label: 'Require human confirmation before merging PR', default: 'true', on_value: 'true', off_value: 'false', on_description: 'Ask first', off_description: 'Auto' },
      ],
    }],
  })} -->\n# Goal`;
  const result = compileTemplateInputs(formHeader, devInputsSchema, {});
  expect(result.status).toBe('ready');
  if (result.status === 'ready') {
    expect(result.input).toEqual({
      baseBranch: 'main',
      testCmd: 'dotnet test',
      requirePrReview: true,
    });
  }
});

it('preserves fractional Unix seconds in upgrade receipts', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
  expect(resolveRef(document, '#/components/schemas/SystemUpgradeReceipt/properties/finishedAt')).toMatchObject({ type: 'number' });
  const finishedAt = 1795080000.125;
  expect(systemUpgradeReceiptSchema.parse({ jobId: 'fractional', oldReleaseId: null,
    newReleaseId: 'new', status: 'succeeded', error: null, rollbackError: null,
    notifyTrackId: null, finishedAt, changed: [] }).finishedAt).toBe(finishedAt);
});

it('matches the eight session primitives to the production Host routes and removes retired DTOs', () => {
  const host = readFileSync(new URL('../../../src/Perga.Host/Http/ApiV1Endpoints.cs', import.meta.url), 'utf8');
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    paths: Record<string, Record<string, unknown>>; components: { schemas: Record<string, unknown> };
  };
  const section = host.split('// ------------------- Unified sessions -------------------')[1].split('// ------------------- Terminals')[0];
  const expected = [...section.matchAll(/v1\.Map(Get|Post|Patch|Delete)\("([^"]+)"/g)]
    .map(match => `${match[1].toLowerCase()} /api/v1${match[2]}`).sort();
  const actual = Object.entries(document.paths).filter(([path]) => path.startsWith('/api/v1/sessions/') || path === '/api/v1/tracks/{trackId}/sessions')
    .flatMap(([path, operations]) => Object.keys(operations).filter(method => METHODS.has(method)).map(method => `${method} ${path}`)).sort();
  expect(expected).toHaveLength(8);
  expect(actual).toEqual(expected);
  for (const name of ['WorkerActivityDto', 'WorkerSettings', 'WorkerDispatchReceipt', 'WorkerMessageReceipt', 'RecoverSessionRequest', 'UpdateSessionModelRequest'])
    expect(document.components.schemas).not.toHaveProperty(name);
});

// These fixtures are serialized by the real C# DTOs with PergaJsonDefaults.ApiOptions.
// Validate the published JSON Schema, including nested refs, rather than the hand-written decoder.
it.each([
  ['CreateSessionRequest', { task: null }],
  ['SessionCreationReceipt', hostPayloads.creation],
  ['SessionCreationReceipt', hostPayloads.emptyCreation],
  ['SessionItemsPageDto', hostPayloads.page],
  ['SessionSummaryDto', hostPayloads.summary],
  ['PendingInteractionDto', hostPayloads.approval],
  ['PendingInteractionDto', hostPayloads.ask],
])('accepts null-emitting Host payloads for %s', (name, payload) => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
  // Zod's JSON Schema reader resolves $defs; preserve each schema and only rebase refs.
  if (!object(document.components) || !object(document.components.schemas)) throw new Error('Missing schemas');
  const definitions = JSON.parse(JSON.stringify(document.components.schemas)
    .replaceAll('#/components/schemas/', '#/$defs/')) as Record<string, z.core.JSONSchema.JSONSchema>;
  const schema = z.fromJSONSchema({ $defs: definitions, $ref: `#/$defs/${name}` });
  expect(schema.parse(payload)).toEqual(payload);
  expect(schema.safeParse(null).success).toBe(false);
});
