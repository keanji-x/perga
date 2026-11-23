import { describe, expect, expectTypeOf, it } from 'vitest';
import type { BuiltinListView, BuiltinTableView, EditorialListItem } from '../../../templates/perga-template.js';
import type { input, output } from 'zod';
import { editorialItemSchema } from './report-editorial.js';
import { builtinListViewSchema, builtinTableViewSchema } from './report-widget.js';
import { parseBuiltinListView, parseBuiltinTableView } from './report-widget-compat.js';

describe('built-in report view payloads', () => {
  const columns = [{ key: 'result', label: 'Result' }];

  it('matches the template authoring types before defaulting omitted fields', () => {
    expectTypeOf<output<typeof builtinTableViewSchema>>().toEqualTypeOf<BuiltinTableView>();
    expectTypeOf<BuiltinTableView>().toExtend<input<typeof builtinTableViewSchema>>();
    expectTypeOf<Omit<input<typeof builtinListViewSchema>, 'items'>>().toEqualTypeOf<Omit<BuiltinListView, 'items'>>();
    expectTypeOf<Omit<input<typeof editorialItemSchema>, 'table'>>().toEqualTypeOf<Omit<EditorialListItem, 'table'>>();
    expectTypeOf<BuiltinListView>().toExtend<input<typeof builtinListViewSchema>>();
  });

  it('accepts sparse rows, ignores undeclared row keys of any type, and defaults omitted list status to pending', () => {
    const rawTable = {
      columns,
      rows: [{}, { result: null }, { result: undefined }, { result: 'ok', extra: 123, nested: { ignored: true } }],
    };
    const parsedTable = builtinTableViewSchema.parse(rawTable);
    expect(parsedTable).toEqual({
      columns,
      rows: [{}, { result: null }, { result: undefined }, { result: 'ok' }],
    });
    expect(builtinListViewSchema.parse({ items: [{ id: 'item', title: 'Item', table: rawTable }] }).items[0]?.status)
      .toBe('pending');
  });

  it.each([
    { columns: [], rows: [] },
    { columns: [...columns, ...columns], rows: [] },
    { columns: [{ key: '__proto__', label: 'Proto' }], rows: [] },
    { columns, rows: [{ result: 123 }] },
    { columns, rows: [{ result: { text: 'Done', check: 'true' } }] },
    { columns, rows: [{ result: { text: 'Done', verdict: 'beat' } }] },
    { metrics: [{ label: 'Old shape' }] },
  ])('rejects malformed tables %#', (value) => {
    expect(builtinTableViewSchema.safeParse(value).success).toBe(false);
  });

  it.each([
    { items: [], metrics: [] },
    { items: [{ id: 'item', title: 'Item', verdict: 'beat' }] },
    { items: [{ id: 'item', title: 'Item', table: { columns: [], rows: [] } }] },
  ])('rejects malformed lists %#', (value) => {
    expect(builtinListViewSchema.safeParse(value).success).toBe(false);
  });

  it('adapts frozen v1 okr-loop and equity-research snapshot payloads while still rejecting malformed inputs', () => {
    const legacyTable = parseBuiltinTableView({
      metrics: [{
        code: 'KR1',
        label: '**Latency**',
        tag: 'CI',
        current: '7.2 min',
        targetLabel: '≤ 8 min',
        status: 'met',
        updatedAt: '11-21 09:00',
      }, {
        label: 'Error rate',
        unit: '%',
        target: 1,
        direction: 'lte',
        points: [{ date: '11-20', value: 1.4 }, { date: '11-21', value: 0.8 }],
      }, {
        label: 'No direction metric',
        unit: 'ms',
        target: 100,
        points: [{ date: '11-20', value: 90 }, { date: '11-21', value: 80.124 }],
      }],
    });
    expect(legacyTable.success).toBe(true);
    if (legacyTable.success) {
      expect(legacyTable.data.columns.map((c) => c.label)).toEqual(['Key Result', '当前', '目标', '更新时间']);
      expect(legacyTable.data.rows[0]).toEqual({
        result: { text: '**Latency**', code: 'KR1', tag: 'CI', markdown: true },
        current: { text: '7.2 min', tone: 'good', check: true },
        target: '≤ 8 min',
        updatedAt: '11-21 09:00',
      });
      expect(legacyTable.data.rows[1]).toEqual({
        result: { text: 'Error rate', markdown: true },
        current: { text: '0.8%', tone: 'good', check: true },
        target: '≤ 1%',
        updatedAt: '11-21',
      });
      expect(legacyTable.data.rows[2]).toEqual({
        result: { text: 'No direction metric', markdown: true },
        current: { text: '80.1 ms', tone: 'good', check: true },
        target: null,
        updatedAt: '11-21',
      });
    }
    expect(parseBuiltinTableView({ metrics: [{ label: 123 }] }).success).toBe(false);
    expect(parseBuiltinTableView({ metrics: Array.from({ length: 13 }, (_, i) => ({ label: `M${i}` })) }).success)
      .toBe(false);

    const legacyList = parseBuiltinListView({
      metrics: [{ label: 'Top KR', current: '90%', targetLabel: '≥ 90%', status: 'met' }],
      items: [{
        id: 'evt-1',
        date: '2026-11-21',
        title: 'Q3 Earnings',
        status: 'done',
        verdict: '超预期',
        taskKey: 't-earnings',
        model: 'gpt-6.2-sol',
        assign: 'gpt-6.2-sol',
        assignKind: 'model',
        triggerAt: 'a'.repeat(1024),
        docLabel: 'b'.repeat(1024),
        summary: 'Revenue beat consensus',
        expectations: [{ label: 'Revenue', expected: '$35B', actual: '$36.2B', status: 'beat' }],
      }],
    });
    expect(legacyList.success).toBe(true);
    if (legacyList.success) {
      expect(legacyList.legacyMetricsTable?.rows[0]?.result).toEqual({ text: 'Top KR', markdown: true });
      const item = legacyList.data.items[0];
      expect(item?.badge).toBe('超预期');
      expect(item?.tone).toBe('good');
      expect(item?.meta).toBe('gpt-6.2-sol');
      expect(item?.subtitle).toBe('a'.repeat(1024));
      expect(item?.table?.columns.map((c) => c.label)).toEqual(['指标', '事前预期', '实际兑现', '判定']);
      expect(item?.table?.rows[0]?.status).toEqual({ text: '超预期', tone: 'good' });
    }
    expect(parseBuiltinListView({ items: [{ id: 'bad', title: 'Broken', status: 'unknown' }] }).success).toBe(false);
    expect(parseBuiltinListView({ items: [{ id: 'bad', title: 'Broken', status: 'done', bogus: 1 }] }).success).toBe(false);
  });
});
