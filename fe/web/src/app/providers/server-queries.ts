import { z } from 'zod';
import { bootstrapDtoSchema } from '../../../../core/api/perga-v1.ts';
import type { ApiOperation } from '../../../../core/api/types.ts';
import { WEB_COMPAT_VERSION,type ServerVersionInfo } from './public.tsx';

const serverVersionSchema = z.object({
  pergaV1: z.boolean().optional(),
  authRequired: z.boolean().optional(),
  webCompatVersion: z.number(),
  minWebCompatVersion: z.number(),
  syncEventVersion: z.number(),
  dbInstanceId: z.string(),
  databaseId: z.string().optional(),
});

export function serverVersionOperation(): ApiOperation<ServerVersionInfo> {
  return {
    method: 'GET',
    path: '/api/v1/bootstrap',
    responseSchema: z.union([
      bootstrapDtoSchema.transform((bootstrap): ServerVersionInfo => ({
        pergaV1: true,
        authRequired: bootstrap.authRequired ?? true,
        webCompatVersion: WEB_COMPAT_VERSION,
        minWebCompatVersion: WEB_COMPAT_VERSION,
        syncEventVersion: 1,
        dbInstanceId: bootstrap.dbInstanceId ?? `perga-${bootstrap.version}`,
        ...(bootstrap.databaseId !== null && bootstrap.databaseId !== undefined ? { databaseId: bootstrap.databaseId } : {}),
      })),
      serverVersionSchema,
    ]),
  };
}
