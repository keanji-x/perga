import { z } from 'zod';
import { flowProjectionSchema } from './report-flow.js';
import { max2048CodePoints } from './report-table.js';

export const WIDGET_MIN_HEIGHT = 48;
export const WIDGET_MAX_HEIGHT = 2400;

export const widgetBlockPayloadSchema = z.strictObject({
  /** `builtin:flow` | `builtin:stepper` | `builtin:list` | `builtin:table` | `builtin:detail` | `./widgets/<name>.html` */
  use: max2048CodePoints(z.string().min(1)),
  /** View key declared in template `report.views` (or `flow` for `builtin:flow`) */
  view: max2048CodePoints(z.string().min(1)),
  /** Default `seamless` for HTML widgets (zero white box, transparent bg, auto-height). */
  chrome: z.enum(['seamless', 'framed']).optional(),
  /** Optional report-scoped key linking a graphic widget's `select` intent to a native `builtin:detail` block. */
  selectionKey: max2048CodePoints(z.string().min(1)).optional(),
  title: max2048CodePoints(z.string()).nullish(),
  height: z.number().min(WIDGET_MIN_HEIGHT).max(WIDGET_MAX_HEIGHT).nullish(),
});

export type WidgetBlockPayload = z.infer<typeof widgetBlockPayloadSchema>;

export const templateViewDiagnosticSchema = z.object({
  view: z.string(),
  message: z.string(),
});

export const templateViewsProjectionSchema = z.object({
  templateId: z.string().default(''),
  views: z.record(z.string(), z.unknown()).default({}),
  widgets: z.record(z.string(), z.string()).default({}),
  dataRev: z.number().int().nonnegative().default(0),
  flowRev: z.number().int().nonnegative().default(0),
  flow: flowProjectionSchema.nullish(),
  diagnostics: z.array(templateViewDiagnosticSchema).default([]),
  humanReadOnly: z.boolean().default(false),
});

export type TemplateViewsProjection = z.infer<typeof templateViewsProjectionSchema>;

export type TemplateViewsState =
  | Readonly<{ status: 'loading' }>
  | Readonly<{ status: 'error'; message: string }>
  | Readonly<{ status: 'ready'; projection: TemplateViewsProjection }>;
