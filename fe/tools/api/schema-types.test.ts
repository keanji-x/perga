import { readFileSync } from 'node:fs';
import { expect, it } from 'vitest';
import { assertWireCurrent, generateWire, schemaType } from './schema-types.mjs';

it('preserves reference, nullable enum, array, dictionary and required property structure', () => {
  expect(schemaType({ type: 'object', required: ['id'], properties: {
    id: { type: 'string' }, values: { type: 'array', items: { anyOf: [
      { enum: ['ready', 'failed'] }, { type: 'null' },
    ] } }, child: { $ref: '#/components/schemas/Child' },
  } })).toBe('{ "id": string; "values"?: Array<"ready" | "failed" | null>; "child"?: Child }');
  expect(schemaType({ type: 'object', additionalProperties: { type: 'integer' } })).toBe('{ [key: string]: number }');
  expect(schemaType({})).toBe('unknown');
  expect(schemaType({ type: 'object', additionalProperties: false })).toBe('Record<string, never>');
});

it('fails closed on unsupported unions rather than emitting an unconstrained type', () => {
  expect(() => schemaType({ oneOf: [{ type: 'string' }, { type: 'integer' }] })).toThrow('Unsupported Host schema');
});

it('fails closed on external references rather than emitting a misleading local type', () => {
  expect(() => schemaType({ $ref: 'https://example.test/schema/Child' })).toThrow('Unsupported reference');
});

it('checks the complete committed wire module, including field types, enums and nullability', () => {
  const document = JSON.parse(readFileSync(new URL('../../core/api/generated/openapi.json', import.meta.url), 'utf8')) as {
    components: { schemas: Parameters<typeof generateWire>[0] };
  };
  const source = readFileSync(new URL('../../core/api/generated/wire.ts', import.meta.url), 'utf8');
  expect(() => assertWireCurrent(document.components.schemas, source)).not.toThrow();
});

it.each([
  [{ type: 'integer' }, 'number', 'string'],
  [{ anyOf: [{ type: 'string' }, { type: 'null' }] }, ' | null', ''],
  [{ type: 'string', enum: ['ready', 'failed'] }, '"failed"', '"other"'],
] as const)('rejects a single wire-only structural drift: %j', (schema, original, replacement) => {
  const schemas = { Value: schema };
  const source = generateWire(schemas);
  expect(source).toContain(original);
  expect(() => assertWireCurrent(schemas, source.replace(original, replacement))).toThrow('wire.ts drifted');
});
