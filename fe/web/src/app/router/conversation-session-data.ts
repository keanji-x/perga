import type { AgentSessionDto, SessionInputReceipt, SessionItemDto, SessionItemsPageDto, SessionTurnDto } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';

export const sessionReply = (body: unknown): ApiTransportResponse => ({ status: 200, statusText: 'OK', body });

export function sessionItem(sessionId: string, id: number, text: string, kind = 'assistant_text', turnId = 'turn-1'): SessionItemDto {
  return { id, sessionId, turnId, kind, payload: kind === 'user' ? { content: text, inputId: `input-${id}` } : { text }, createdAtMs: id };
}

export function sessionTurn(sessionId: string, id = 'turn-1', status = 'running'): SessionTurnDto {
  return { id, sessionId, turnIndex: 1, epoch: 1, triggerKind: 'user', status, startedAtMs: Date.now(), completedAtMs: null };
}

export function sessionInput(sessionId: string, inputId = 'input-2'): SessionInputReceipt {
  return { sessionId, inputId, turnId: 'turn-1', deliveryState: 'accepted', turnStatus: 'queued',
    statusUrl: `/api/v1/sessions/${sessionId}/inputs/${inputId}` };
}

export function sessionPage(session: AgentSessionDto, items: SessionItemDto[] = [], activeTurn: SessionTurnDto | null = null,
  unknownInput: SessionInputReceipt | null = null): SessionItemsPageDto {
  return { session, items, activeTurn, unknownInput };
}
