import { agentSessionDtoSchema } from '../api/perga-v1.js';
import { agentSessionDtoToConversation, createTrackConversationOperation } from './conversation.js';
import { modelCatalogOperation } from './conversation-models.js';
import { createTrackOperation } from './track.js';
import { expect, it } from 'vitest';
import { agentProvidersOperation, managedProviderProblem, managedProvidersOperation } from './agent-providers.js';

const bootstrap = Object.freeze({ version: 'test', providers: [
  { provider: 'claude', available: true, version: '1.0', reason: null, models: [] },
  { provider: 'codex', available: false, version: null, reason: 'Sign in first', models: [] },
  { provider: 'acp:opencode', available: true, version: '2.0', reason: null, models: [] },
  { provider: 'unregistered', available: true, version: null, reason: null, models: [] },
], templates: [], areas: [] });

it('reads only v1 bootstrap and preserves every registered runtime provider identifier', () => {
  const operation = managedProvidersOperation(true);
  expect(operation.path).toBe('/api/v1/bootstrap?scope=managed&refresh=true');
  const answers = operation.responseSchema.parse(bootstrap);
  expect(answers.map(({ availability }) => availability.provider)).toEqual(['claude', 'codex', 'acp:opencode', 'unregistered']);
  expect(answers[0].availability).toMatchObject({ status: 'ready', authentication_method: '1.0', authentication_notice: null });
  expect(answers[1].availability).toMatchObject({ status: 'unavailable', reason: 'Sign in first' });
  expect(managedProviderProblem(answers, 'codex')).toBeNull();
  expect(managedProviderProblem(answers, 'grok')).toMatch(/unavailable/);
  expect(managedProviderProblem(undefined, 'codex')).toMatch(/unavailable/);
});

it('rejects old availability arrays and managed_conversation responses', () => {
  const answers = managedProvidersOperation(false).responseSchema.parse(bootstrap);
  expect(managedProvidersOperation(false).responseSchema.safeParse(answers).success).toBe(false);
  expect(agentProvidersOperation(false).responseSchema.safeParse(answers.map(answer => answer.availability)).success).toBe(false);
  expect(managedProvidersOperation(false).responseSchema.safeParse({ managed_conversation: answers }).success).toBe(false);
});

it('rejects malformed v1 provider status and requires the bootstrap version', () => {
  for (const operation of [managedProvidersOperation(false), agentProvidersOperation(false)]) {
    expect(operation.responseSchema.safeParse({ providers: bootstrap.providers }).success).toBe(false);
    expect(operation.responseSchema.safeParse({ ...bootstrap, providers: [{ provider: 'codex', available: 'yes' }] }).success).toBe(false);
  }
});


it('reads remote provider status arrays and retains refresh and target identity', () => {
  const target = 'ssh/user &host';
  for (const refresh of [false, true]) {
    const availability = agentProvidersOperation(refresh, target);
    const managed = managedProvidersOperation(refresh, target);
    const path = '/api/v1/targets/ssh%2Fuser%20%26host/providers' + (refresh ? '?refresh=true' : '');
    expect(availability.path).toBe(path);
    expect(managed.path).toBe(path);
    expect(availability.responseSchema.parse(bootstrap.providers).map(item => item.provider))
      .toEqual(['claude', 'codex', 'acp:opencode', 'unregistered']);
    expect(managed.responseSchema.parse(bootstrap.providers)[1].availability)
      .toMatchObject({ provider: 'codex', status: 'unavailable', reason: 'Sign in first' });
    expect(availability.responseSchema.safeParse(bootstrap).success).toBe(false);
    expect(managed.responseSchema.safeParse(bootstrap).success).toBe(false);
  }
});

it.each(['acp:opencode', 'acp:antigravity', 'vendor/new &mode=other'])('round-trips runtime provider %s through selection, launch and session reads', provider => {
  const input = { version: 'runtime', providers: [{ provider, available: true, models: ['model'] }] };
  const selected = managedProvidersOperation(false).responseSchema.parse(input)[0].availability.provider;
  expect(selected).toBe(provider);
  expect(createTrackOperation({ area_id: 'area', planner_provider: selected,
    theme: { fg: [0, 0, 0], bg: [255, 255, 255] } }, 'key').body).toMatchObject({ provider });
  expect(createTrackConversationOperation('track', 'Inspect', 'key', selected,
    { model: null, reasoning_effort: null }).body).toMatchObject({ provider });
  const catalog = modelCatalogOperation({ kind: 'provider', provider: selected });
  expect(catalog.path).toBe(`/api/v1/bootstrap?provider=${encodeURIComponent(provider)}`);
  expect(catalog.responseSchema.parse(input).models[0].model).toBe('model');
  const session = agentSessionDtoSchema.parse({ id: 'session', trackId: 'track', provider,
    contextUsage: null, createdAtMs: 1, updatedAtMs: 2 });
  expect(agentSessionDtoToConversation(session).provider).toBe(provider);
});
