import { z } from 'zod';

const terminalPresentationSchema = z.object({
  source: z.enum(['manual', 'agent', 'task', 'system', 'plugin', 'unrecorded']),
  in_workbench: z.boolean(),
});
const terminalPayloadSchema = z.object({ terminal_presentation: terminalPresentationSchema });
export type TerminalPresentation = z.infer<typeof terminalPresentationSchema>;

/** Workbench cards carry explicit placement. Terminals without recorded provenance use unrecorded. */
export function terminalPresentation(card: Readonly<{ kind: string; payload: unknown }>): TerminalPresentation {
  if (card.kind !== 'terminal') throw new Error('Terminal presentation requires a terminal card');
  return terminalPayloadSchema.parse(card.payload).terminal_presentation;
}
