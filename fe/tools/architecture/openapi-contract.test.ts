import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { describe,expect,it } from 'vitest';
import { z } from 'zod';
import { loginRequestSchema, sessionIdentitySchema } from '../../core/api/auth.ts';
import hostPayloads from '../../core/api/fixtures/unified-host-payloads.json';
import { createSessionRequestSchema, systemUpgradeReceiptSchema } from '../../core/api/perga-v1.ts';
import { compilePergaTemplateInputs, parsePergaInputsSchema } from '../../core/domain/template-input.ts';

type JsonObject = Record<string, unknown>;

const METHODS = new Set(['delete', 'get', 'head', 'options', 'patch', 'post', 'put', 'trace']);
const PATH_ITEM_FIELDS = new Set([...METHODS, 'summary', 'description', 'servers', 'parameters']);
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
    .filter((name) => !wireTypes.has(name))
    .sort();
  if (missing.length > 0) throw new Error(`response schema wire types missing: ${missing.join(', ')}`);
}

describe('generated OpenAPI integrity', () => {
  it('keeps every operation unambiguous, referenced, complete, and wire-covered', () => {
    const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
    const wire = readFileSync(new URL('../../core/api/generated/wire.ts', import.meta.url), 'utf8');
    validateDocument(document, wire);
  });

  it('publishes flow snapshots and the optional inline JSON commit input', () => {
    const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
    expect(document.paths).toMatchObject({
      '/api/v1/tracks/{trackId}/flow': {
        get: { responses: { 200: { description: 'HTTP 200' } } },
      },
      '/api/v1/tracks/{trackId}/flow/commit': {
        post: { requestBody: { required: false, content: { 'application/json': {
          schema: { $ref: '#/components/schemas/FlowCommitRequest' },
        } } } },
      },
    });
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
  expect(SessionContextUsage.required).toEqual(['observedAtMs', 'ratio', 'usedTokens', 'windowTokens']);
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
    '/api/v1/tracks/{trackId}/template',
    '/api/v1/tracks/{trackId}/files',
    '/api/v1/tracks/{trackId}/vcs-diff',
    '/api/v1/tracks/{trackId}/report',
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
  expect(document.paths['/api/v1/tracks/{trackId}/tasks/{taskKey}/attempts']).toBeUndefined();
});

it('publishes effective auth security for every operation', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    security: unknown; paths: Record<string, Record<string, JsonObject>>;
    components: { securitySchemes: Record<string, unknown> };
  };
  const cookie = { pergaSessionCookie: [] };
  const bearer = { systemManagementBearer: [] };
  const overrides = {
    'GET /healthz': [],
    'POST /api/v1/auth/login': [],
    'POST /api/v1/auth/logout': [cookie, {}],
    'GET /api/v1/system/info': [cookie, bearer],
    'GET /api/v1/system/auth-status': [bearer],
    'POST /api/v1/system/drain': [bearer],
    'POST /api/v1/system/reload': [bearer],
  } satisfies Record<string, unknown>;

  expect(document.security).toEqual([cookie]);
  expect(document.components.securitySchemes).toMatchObject({
    pergaSessionCookie: { type: 'apiKey', in: 'cookie', name: 'perga_session' },
    systemManagementBearer: { type: 'http', scheme: 'bearer' },
  });
  const explicit = [];
  for (const [path, operations] of Object.entries(document.paths)) {
    for (const [method, operation] of Object.entries(operations)) {
      if (!METHODS.has(method)) continue;
      const route = `${method.toUpperCase()} ${path}`;
      const expected = overrides[route as keyof typeof overrides] ?? [cookie];
      expect(operation.security ?? document.security, route).toEqual(expected);
      if (operation.security !== undefined) explicit.push(route);
    }
  }
  expect(explicit.sort()).toEqual(Object.keys(overrides).sort());
  expect(document.paths['/api/v1/auth/session'].get).not.toHaveProperty('security');
});

it('matches auth schemas, response statuses, and headers to the Host wire contract', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
  const schema = (name: string) => z.fromJSONSchema(resolveRef(document, `#/components/schemas/${name}`) as z.core.JSONSchema.JSONSchema);
  const identity = { sessionId: 'aus_1', userId: 'usr_1', username: 'owner', displayName: 'Owner', role: 'owner', expiresAtMs: 123456789 };
  for (const decoder of [schema('SessionIdentityDto'), sessionIdentitySchema]) {
    expect(decoder.parse(identity)).toEqual(identity);
    for (const key of Object.keys(identity) as (keyof typeof identity)[]) {
      const partial: Partial<typeof identity> = { ...identity };
      delete partial[key];
      expect(decoder.safeParse(partial).success, key).toBe(false);
    }
  }
  const credentials = { username: 'owner', password: 'example password' };
  for (const decoder of [schema('LoginRequest'), loginRequestSchema]) {
    expect(decoder.parse(credentials)).toEqual(credentials);
    expect(decoder.safeParse({ username: 'owner' }).success).toBe(false);
    expect(decoder.safeParse({ ...credentials, sessionId: 'extra' }).success).toBe(false);
  }
  const apiError = schema('ApiErrorResponse');
  const error = { code: 'insecure_transport_required', error: 'An HTTPS connection is required.' };
  expect(apiError.parse({ ...error, nextStep: 'Use HTTPS.' })).toEqual({ ...error, nextStep: 'Use HTTPS.' });
  expect(apiError.safeParse({ ...error, nextStep: null }).success).toBe(true);
  expect(apiError.safeParse({ ...error, nextStep: 42 }).success).toBe(false);
  const status = schema('SystemAuthStatusDto');
  expect(status.safeParse({
    schemaVersion: 26, configured: false, username: null, displayName: null, disabled: false, activeSessions: 0,
  }).success).toBe(true);
  expect(status.safeParse({ schemaVersion: 26, configured: false }).success).toBe(false);

  const login = resolveRef(document, '#/paths/~1api~1v1~1auth~1login/post/responses');
  expect(Object.keys(login as JsonObject)).toEqual(['200', '400', '401', '403', '413', '415', '429', 'default']);
  expect(login).toMatchObject({
    200: { headers: { 'X-Perga-Session-Id': { schema: { type: 'string' } }, 'Set-Cookie': { schema: { type: 'string' } } } },
    429: { headers: { 'Retry-After': { schema: { type: 'integer' } } } },
  });
  expect(resolveRef(document, '#/paths/~1api~1v1~1auth~1logout/post/parameters')).toMatchObject([
    { name: 'X-Perga-Session-Id', in: 'header', required: false },
  ]);
});

it('compiles input defaults from the bundled agents-delivery template', () => {
  const source = readFileSync(new URL('../../../templates/agents-delivery/template.perga.js', import.meta.url), 'utf8');
  const definition: unknown = runInNewContext(source.replace(/^export default /m, ''), { defineTemplate: (value: unknown) => value });
  const template = z.object({ id: z.literal('agents-delivery'), inputs: z.unknown() }).parse(definition);
  expect(compilePergaTemplateInputs(parsePergaInputsSchema(template.inputs), {})).toEqual({
    valid: true, input: { testCmd: 'dotnet test Perga.sln' }, errors: {},
  });
});

it('matches the Host track report, task, template and flow operations', () => {
  const host = readFileSync(new URL('../../../src/Perga.Host/Http/ApiV1Endpoints.cs', import.meta.url), 'utf8');
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    paths: Record<string, Record<string, unknown>>;
  };
  const governedTrackPath = /^\/api\/v1\/tracks\/\{trackId\}\/(report|tasks|template|flow|schedules)(?:\/|$)/;
  const expected = [...host.matchAll(/v1\.Map(Get|Post|Patch|Delete|Put)\("([^"]+)"/g)]
    .map(match => ({ method: match[1].toLowerCase(), path: `/api/v1${match[2]}` }))
    .filter(({ path }) => governedTrackPath.test(path))
    .map(({ method, path }) => `${method} ${path}`).sort();
  const actual = Object.entries(document.paths).filter(([path]) => governedTrackPath.test(path))
    .flatMap(([path, operations]) => Object.keys(operations).filter(method => METHODS.has(method)).map(method => `${method} ${path}`)).sort();
  expect(expected).toContain('post /api/v1/tracks/{trackId}/tasks/{taskKey}/cancel');
  expect(actual).toEqual(expected);
});

it('preserves fractional Unix seconds in upgrade receipts', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
  expect(document.paths).not.toHaveProperty('/api/v1/system/upgrade');
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
  for (const name of ['WorkerActivityDto', 'WorkerSettings', 'WorkerDispatchReceipt', 'WorkerMessageReceipt', 'RecoverSessionRequest', 'UpdateSessionModelRequest', 'TaskAttemptDto', 'TaskAttemptView', 'TaskRecoveryView'])
    expect(document.components.schemas).not.toHaveProperty(name);
});

// These fixtures are serialized by the real C# DTOs with PergaJsonDefaults.ApiOptions.
// Validate the published JSON Schema, including nested refs, rather than the hand-written decoder.
it.each([
  ['CreateSessionRequest', { title: null, prompt: 'Inspect the workspace', notifyParent: null }],
  ['SessionCreationReceipt', hostPayloads.creation],
  ['SessionCreationReceipt', hostPayloads.emptyCreation],
  ['SessionItemsPageDto', hostPayloads.page],
  ['SessionSummaryDto', hostPayloads.summary],
  ['TaskDto', hostPayloads.task],
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


it('limits collaborator creation to turn notifications and plain session fields', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as JsonObject;
  const published = resolveRef(document, '#/components/schemas/CreateSessionRequest') as z.core.JSONSchema.JSONSchema;
  expect(createSessionRequestSchema.safeParse({ notifyParent: 'task_settled' }).success).toBe(false);
  // Host DTO schema describes structure; the Kernel owns notification policy.
  for (const schema of [createSessionRequestSchema, z.fromJSONSchema(published)]) {
    expect(schema.safeParse({ prompt: 'Inspect', notifyParent: 'turn_end' }).success).toBe(true);

    expect(schema.safeParse({ task: null }).success).toBe(false);
    expect(schema.safeParse({ role: null }).success).toBe(false);
  }
});

it('publishes task-owned sessions and epoch message guards without attempt fields', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: Record<string, { properties: Record<string, unknown> }> };
  };
  const schemas = document.components.schemas;
  expect(Object.keys(schemas.TaskDto.properties)).toEqual(expect.arrayContaining([
    'sessionId', 'runCount', 'workerResult', 'meta', 'gateVerdict', 'startedAtMs', 'finishedAtMs',
    'outcome', 'outcomeSummary', 'triggerRunKey', 'archivedAtMs',
  ]));
  expect(schemas.TaskDto.properties).not.toHaveProperty('latestAttemptId');
  expect(schemas.AgentSessionDto.properties).not.toHaveProperty('boundTaskId');
  expect(schemas.AgentSessionDto.properties).not.toHaveProperty('boundAttemptId');
  expect(schemas.SessionSummaryDto.properties).toHaveProperty('taskId');
  expect(Object.keys(schemas.SessionTaskBindingDto.properties).sort()).toEqual(['phase', 'task', 'waitingReason']);
  for (const name of ['UpdateSessionRequest', 'SendSessionMessageRequest', 'SessionControlRequest'])
    expect(schemas[name].properties).not.toHaveProperty('expectedAttemptId');
  expect(schemas.SendSessionMessageRequest.properties).toHaveProperty('expectedEpoch');
  expect(schemas.TerminalDto.properties).toHaveProperty('sessionId');
  expect(schemas.TerminalDto.properties).not.toHaveProperty('attemptId');
  expect(schemas).not.toHaveProperty('DbMaintenanceStatsDto'); // Not exposed by HTTP.
});
