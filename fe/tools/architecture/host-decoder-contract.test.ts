import { readFileSync } from 'node:fs';
import { expect, it } from 'vitest';
import { z } from 'zod';
import * as decoders from '../../core/api/perga-v1.ts';
import { dailyTrackSchema, reportChangesSchema, reportEditsSchema, todayCalendarSchema } from '../../core/domain/daily-planner.ts';

it('keeps runtime DTO decoder fields aligned with the independently exported Host types', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: Record<string, { properties?: Record<string, unknown> }> };
  };
  const names: Readonly<Record<string, string>> = {
    providerStatusDtoSchema: 'ProviderStatus', providerModelInfoDtoSchema: 'ProviderModelInfo',
    providerModelEffortDtoSchema: 'ProviderModelEffort', permissionModeOptionDtoSchema: 'PermissionModeOption',
    compiledTemplateManifestDtoSchema: 'CompiledTemplateManifest', trackAggregateSnapshotDtoSchema: 'TrackAggregateSnapshot',
    reportReadResultDtoSchema: 'ReportReadResult', reportDocumentDtoSchema: 'ReportDocument',
  };
  for (const typeName of ['AreaDto', 'TrackDto', 'ProviderStatus', 'TrackAggregateSnapshot', 'DailyTrackDto', 'TodayCalendarDto'])
    expect(document.components.schemas[typeName], typeName).toBeDefined();
  let compared = 0;
  for (const [name, decoder] of Object.entries(decoders)) {
    const input = decoder instanceof z.ZodPipe ? decoder.in : decoder;
    if (!(input instanceof z.ZodObject) || !name.endsWith('DtoSchema')) continue;
    const typeName = names[name] ?? name[0].toUpperCase() + name.slice(1, -6);
    const published = document.components.schemas[typeName];
    if (published?.properties === undefined) continue; // WS-only DTOs have no HTTP root.
    expect.soft(Object.keys(input.shape).sort(), typeName).toEqual(Object.keys(published.properties).sort());
    compared += 1;
  }
  for (const [typeName, decoder] of [
    ['DailyTrackDto', dailyTrackSchema], ['TodayCalendarDto', todayCalendarSchema],
    ['DailyReportChangesPageDto', reportChangesSchema], ['DailyReportEditsPageDto', reportEditsSchema],
  ] as const) {
    expect(Object.keys(decoder.shape).sort(), typeName).toEqual(Object.keys(document.components.schemas[typeName].properties ?? {}).sort());
  }
  expect(compared).toBeGreaterThan(10);
});
