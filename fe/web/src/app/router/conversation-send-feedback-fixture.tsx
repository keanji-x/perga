import type { AgentSessionDto } from '../../../../core/api/perga-v1.ts';
import type { ApiTransportResponse } from '../../../../core/api/types.ts';
import { fixtureCreation, fixtureSession } from './session-contract-fixture.ts';
import { renderSessionFixture } from './conversation-session-fixture.tsx';
import { sessionInput, sessionItem, sessionReply } from './conversation-session-data.ts';

/** The server chooses the session ID; retries replay the original receipt. */
export function renderSendFeedbackFixture({ held = false, lost = false, refused = false, strict = false } = {}) {
  let release: (() => void) | undefined;
  let refusal = refused;
  let loseResponse = lost;
  const waiting = new Promise<void>(resolve => { release = resolve; });
  const accepted = new Map<string, { session: AgentSessionDto; receipt: ReturnType<typeof sessionInput> }>();
  const view = renderSessionFixture({ strict, initial: '/track/daily?panel=conversations',
    identity: { userId: 'local-owner', displayName: 'Owner', role: 'owner', sessionId: 'feedback' },
    reply: async request => {
      if (request.method !== 'POST' || !(request.path.endsWith('/messages') || request.path.endsWith('/sessions'))) return undefined;
      const key = request.headers?.['Idempotency-Key'];
      if (key === undefined) throw new Error('A send requires an idempotency key');
      const body = request.body as { content?: string; prompt?: string };
      const text = body.content ?? body.prompt ?? '';
      if (held) await waiting;
      if (refusal) return { status: 429, statusText: 'Too Many Requests', body: { code: 'rate_limited', error: 'Wait before retrying.' } };
      let saved = accepted.get(key);
      if (saved === undefined) {
        const creating = request.path.endsWith('/sessions');
        const session = creating ? fixtureSession('server-chosen-session', 'daily', { title: text }) : view.sessions[0];
        if (creating) view.sessions.push(session);
        saved = { session, receipt: sessionInput(session.id, `input-${key}`) };
        accepted.set(key, saved);
        view.items.set(session.id, [...(view.items.get(session.id) ?? []), {
          ...sessionItem(session.id, 2, text, 'user'), payload: { content: text, inputId: saved.receipt.messageId },
        }]);
      }
      if (loseResponse) { loseResponse = false; throw new Error('Creation reply lost after commit'); }
      const response: ApiTransportResponse = request.path.endsWith('/sessions')
        ? { status: 201, statusText: 'Created', body: { ...fixtureCreation(saved.session), firstInput: { ...fixtureCreation(saved.session).firstInput!, inputId: saved.receipt.messageId } } }
        : sessionReply(saved.receipt);
      return response;
    },
  });
  view.items.set('daily-planner', [sessionItem('daily-planner', 1, 'Retained reply.')]);
  return { ...view, accepted, allow: () => { refusal = false; }, pauseSend: () => { held = true; }, release: () => { held = false; release?.(); } };
}
