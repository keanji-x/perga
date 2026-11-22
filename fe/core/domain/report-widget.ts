import { z } from 'zod';
import { checkpointItemSchema, checkpointMetricSchema } from './report-checkpoints.js';
import { flowProjectionSchema } from './report-flow.js';
import { max2048CodePoints } from './report-table.js';

export const WIDGET_MIN_HEIGHT = 48;
export const WIDGET_MAX_HEIGHT = 2400;

export const widgetBlockPayloadSchema = z.strictObject({
  /** `builtin:flow` | `builtin:list` | `builtin:table` | `./widgets/<name>.html` */
  use: max2048CodePoints(z.string().min(1)),
  /** View key declared in template `report.views` (or `flow` for `builtin:flow`) */
  view: max2048CodePoints(z.string().min(1)),
  /** Default `seamless` for HTML widgets (zero white box, transparent bg, auto-height). */
  chrome: z.enum(['seamless', 'framed']).optional(),
  /** Legacy field accepted when decoding stored widget blocks. */
  selectionKey: max2048CodePoints(z.string().min(1)).optional(),
  title: max2048CodePoints(z.string()).nullish(),
  height: z.number().min(WIDGET_MIN_HEIGHT).max(WIDGET_MAX_HEIGHT).nullish(),
});

export type WidgetBlockPayload = z.infer<typeof widgetBlockPayloadSchema>;

export const builtinListViewSchema = z.object({
  items: z.array(checkpointItemSchema).max(100),
  title: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
  groups: z.array(max2048CodePoints(z.string().min(1))).max(100).nullish(),
  metrics: z.array(checkpointMetricSchema).min(1).max(12).nullish(),
});

export const builtinTableViewSchema = z.object({
  metrics: z.array(checkpointMetricSchema).max(12),
});

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
});

export type TemplateViewsProjection = z.infer<typeof templateViewsProjectionSchema>;

export type TemplateViewsState =
  | Readonly<{ status: 'loading' }>
  | Readonly<{ status: 'error'; message: string }>
  | Readonly<{ status: 'ready'; projection: TemplateViewsProjection }>;
