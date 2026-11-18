import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import type { AgentProvider } from '../../../../core/api/generated/wire.ts';
import { historicalConversationRun } from './conversation-read-data.ts';
import { renderDailyFixture } from './daily-planner-fixture.tsx';

const ok = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });
export const steerAnswer = (provider: AgentProvider, steer: boolean) => [{
  availability: { provider, status: 'ready', reason: null, checked_at_ms: 1, authentication_method: null, authentication_notice: null },
  accepts_unavailable_blank_track: true,
  ...(provider === 'codex' ? {
    permission_modes: [
      { id: 'never', label: 'Never', description: 'Sandbox only; never asks.', tone: 'neutral', isDefault: true },
      { id: 'ask', label: 'Ask me', description: 'Pauses the turn to ask you.', tone: 'warning', isDefault: false },
      { id: 'full', label: 'Full access', description: "No sandbox; never asks. Runs with your account's access.", tone: 'danger', isDefault: false },
    ],
  } : {}),
  capabilities: { managed_conversation: true, steer, images: true, manual_compact: true,
    history_replacement: true, requires_ready_availability: false },
}];
export function mountSteerFixture(provider: AgentProvider, managed: () => Promise<ApiTransportResponse> | ApiTransportResponse) {
  return renderDailyFixture({ initial: '/track/daily?panel=conversations', plannerProvider: provider, reply: request => {
    if (request.path.startsWith('/api/v1/bootstrap?scope=managed')) return managed();
    if (request.path.includes('&view=run')) return ok(historicalConversationRun({ card_id: 'daily-planner', worker_session_id: 'runtime',
      phase: 'turn_running', model: null, reasoning_effort: null, permission_mode: 'never', blocked_reason: null,
      manual_compact_supported: true, recovery: null, compact_outcome: null, context_usage_invalidated: false, running_turn: null,
      pending: [{ entry_id: 'queued', rev: 1, text: 'Queued words', queued_at_ms: 1 }], pending_overflow: 0 }, 'running', true));
    return undefined;
  } });
}
