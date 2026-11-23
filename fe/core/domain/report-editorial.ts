import { z } from 'zod';
export function max2048CodePoints(schema: z.ZodString) {
  return schema.refine(value => Array.from(value).length <= 2048, 'Must contain at most 2048 Unicode code points');
}

export const EDITORIAL_STATUSES = Object.freeze(['done', 'running', 'pending', 'blocked'] as const);
export type EditorialListStatus = (typeof EDITORIAL_STATUSES)[number];

export const EDITORIAL_CHECK_STATUSES = Object.freeze(['pass', 'running', 'fail', 'pending'] as const);
export type EditorialCheckStatus = (typeof EDITORIAL_CHECK_STATUSES)[number];

export const EDITORIAL_TONES = Object.freeze(['neutral', 'good', 'warn', 'bad', 'accent'] as const);

export const tableColumnSchema = z.strictObject({
  key: z.string().min(1).max(64).refine((value) => value !== '__proto__', 'Column key cannot be __proto__'),
  label: max2048CodePoints(z.string().min(1)),
  align: z.enum(['left', 'right', 'center']).optional(),
});

export const tableCellContentSchema = z.strictObject({
  text: z.string(),
  tone: z.enum(EDITORIAL_TONES).optional(),
  code: z.string().optional(),
  tag: z.string().optional(),
  check: z.boolean().optional(),
  markdown: z.boolean().optional(),
});

export const tableCellSchema = z.union([z.string(), tableCellContentSchema]);
export type TableCell = z.infer<typeof tableCellSchema>;

export const builtinTableViewSchema = z.strictObject({
  columns: z.array(tableColumnSchema).min(1).max(12),
  rows: z.array(z.record(z.string(), z.unknown())).max(100),
  title: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
}).superRefine(({ columns, rows }, ctx) => {
  const keys = new Set<string>();
  for (const { key } of columns) {
    if (keys.has(key)) {
      ctx.addIssue({ code: 'custom', path: ['columns'], message: 'Column keys must be unique' });
      break;
    }
    keys.add(key);
  }
  rows.forEach((row, rowIndex) => {
    for (const key of keys) {
      if (!Object.hasOwn(row, key)) continue;
      const parsed = tableCellSchema.nullish().safeParse(row[key]);
      if (!parsed.success) {
        for (const issue of parsed.error.issues) {
          ctx.addIssue({ code: 'custom', path: ['rows', rowIndex, key, ...issue.path], message: issue.message });
        }
      }
    }
  });
}).transform((view) => {
  const keys = view.columns.map(({ key }) => key);
  return {
    ...view,
    rows: view.rows.map((row) => {
      const projected: Record<string, TableCell | null | undefined> = {};
      for (const key of keys) {
        if (Object.hasOwn(row, key)) {
          projected[key] = row[key] as TableCell | null | undefined;
        }
      }
      return projected;
    }),
  };
});

const editorialCheckSchema = z.strictObject({
  label: max2048CodePoints(z.string().min(1)),
  status: z.enum(EDITORIAL_CHECK_STATUSES),
  detail: max2048CodePoints(z.string()).nullish(),
  command: max2048CodePoints(z.string()).nullish(),
  output: max2048CodePoints(z.string()).nullish(),
});

export const editorialItemSchema = z.strictObject({
  id: z.string().min(1).max(120),
  title: max2048CodePoints(z.string().min(1)),
  status: z.enum(EDITORIAL_STATUSES).default('pending'),
  date: max2048CodePoints(z.string()).nullish(),
  subtitle: max2048CodePoints(z.string()).nullish(),
  badge: max2048CodePoints(z.string()).nullish(),
  tone: z.enum(EDITORIAL_TONES).nullish(),
  meta: max2048CodePoints(z.string()).nullish(),
  sessionId: z.string().min(1).max(160).nullish(),
  taskKey: z.string().min(1).max(160).nullish(),
  group: max2048CodePoints(z.string()).nullish(),
  summary: max2048CodePoints(z.string()).nullish(),
  bullets: z.array(max2048CodePoints(z.string().min(1))).max(12).nullish(),
  note: max2048CodePoints(z.string()).nullish(),
  table: builtinTableViewSchema.nullish(),
  checks: z.array(editorialCheckSchema).max(16).nullish(),
});

export type BuiltinTableView = z.infer<typeof builtinTableViewSchema>;
export type EditorialCheck = z.infer<typeof editorialCheckSchema>;
export type EditorialListItem = z.infer<typeof editorialItemSchema>;
