import type { PatchSettingsRequestDto, SettingsSnapshotDto } from './generated/wire.js';
import { settingsSnapshotDtoSchema } from './schemas.js';
import type { ApiOperation } from './types.js';

const SETTINGS_PATH = '/api/v1/settings';

export function getSettings(): ApiOperation<SettingsSnapshotDto> {
  return { method: 'GET', path: SETTINGS_PATH, responseSchema: settingsSnapshotDtoSchema, expectedStatuses: [200] };
}

export function patchSettings(body: PatchSettingsRequestDto): ApiOperation<SettingsSnapshotDto> {
  return { method: 'PATCH', path: SETTINGS_PATH, body, responseSchema: settingsSnapshotDtoSchema, expectedStatuses: [200] };
}
