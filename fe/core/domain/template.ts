import { z } from 'zod';
import { compiledTemplateManifestDtoSchema } from '../api/schemas.js';
import type { ApiOperation } from '../api/types.js';

/** Author-supplied display metadata is optional; the actual template body is required. */
export const templateDetailSchema = z.object({
  id: z.string(),
  title: z.string(),
  description: z.string().nullable(),
  instructions: z.string().nullable(),
  body: z.string(),
});
export type TemplateDetail = z.infer<typeof templateDetailSchema>;
export type LoadTemplate = (id: string) => Promise<TemplateDetail>;

export function templateDetailOperation(id: string): ApiOperation<TemplateDetail> {
  return {
    method: 'GET',
    path: `/api/v1/templates?id=${encodeURIComponent(id)}`,
    responseSchema: z.union([
      z.array(compiledTemplateManifestDtoSchema).transform((templates, ctx) => {
        const found = templates.find((candidate) => candidate.id === id);
        if (found === undefined) {
          ctx.addIssue({ code: z.ZodIssueCode.custom, message: `Template ${id} not found` });
          return z.NEVER;
        }
        return {
          id: found.id,
          title: found.title,
          description: found.description,
          instructions: null,
          body: found.initialReportMarkdown || `# ${found.title}`,
        };
      }),
      templateDetailSchema,
    ]),
  };
}

export const templatePluginGuideSchema = z.strictObject({ id: z.string(), name: z.string() });
export type TemplatePluginGuide = z.infer<typeof templatePluginGuideSchema>;
export function templatePluginGuidesOperation(templateId: string): ApiOperation<TemplatePluginGuide[]> {
  return {
    method: 'GET',
    path: `/api/v1/templates?pluginGuides=${encodeURIComponent(templateId)}`,
    responseSchema: z.union([
      z.array(templatePluginGuideSchema),
      z.array(compiledTemplateManifestDtoSchema).transform((templates) => {
        const found = templates.find((candidate) => candidate.id === templateId);
        return (found?.defaultPlugins ?? []).map((pluginId) => ({ id: pluginId, name: pluginId }));
      }),
    ]),
  };
}
