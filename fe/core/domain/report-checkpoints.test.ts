import { describe, expect, it } from 'vitest';
import { readTrackReportPayload } from './report.js';

describe('checkpoints report block', () => {
  it('parses a native checkpoints block with rules, docs, checks, and worker bindings', () => {
    const report = readTrackReportPayload({
      pergaSnapshot: true,
      docRev: 1,
      summary: 'Stage delivery',
      blocks: [
        {
          id: 'b_cp01',
          rev: 1,
          kind: 'checkpoints',
          payload: {
            tier: 'AGENTS.md · L2',
            rules: [{ label: 'Review 模型锁', text: 'role=review 强制 GPT 6.2 SOAR' }],
            items: [
              {
                id: 'design',
                title: '设计',
                status: 'done',
                statusLabel: '已对齐',
                docPath: 'docs/design/checkpoint-native-view.md',
                sessionId: 'ses_w1',
                workerLabel: 'Worker #1',
                model: 'Claude Opus',
              },
              {
                id: 'review-l2',
                title: 'L2 Review',
                status: 'pending',
                dependsOn: ['实现二', 'L1 Review'],
                constraint: 'role=review · 强制模型 GPT 6.2 SOAR',
              },
            ],
          },
        },
      ],
    });

    expect(report?.blocks).toHaveLength(1);
    const block = report!.blocks![0];
    expect(block.kind).toBe('checkpoints');
    if (block.kind === 'checkpoints') {
      expect(block.payload.tier).toBe('AGENTS.md · L2');
      expect(block.payload.items).toHaveLength(2);
      expect(block.payload.items[0].docPath).toBe('docs/design/checkpoint-native-view.md');
      expect(block.payload.items[1].status).toBe('pending');
    }
  });

  it('parses a chronicle layout block with dates, categories, verdicts, and standing triggers', () => {
    const report = readTrackReportPayload({
      summary: 'NVDA Research',
      blocks: [
        {
          id: 'b_chr01',
          rev: 1,
          kind: 'checkpoints',
          payload: {
            layout: 'chronicle',
            triggers: [
              { trigger: 'SEC 10-Q 发布', action: '生成同口径财报拆解', assignKind: 'model', assign: 'Claude Opus' },
            ],
            items: [
              {
                id: 'fy26-q3',
                date: '2026-08',
                category: '财报',
                verdict: '超预期',
                title: 'FY26 Q3 财报拆解',
                status: 'done',
                model: 'Claude Opus',
              },
            ],
          },
        },
      ],
    });
    const block = report?.blocks?.[0];
    expect(block?.kind).toBe('checkpoints');
    if (block?.kind === 'checkpoints') {
      expect(block.payload.layout).toBe('chronicle');
      expect(block.payload.triggers).toHaveLength(1);
      expect(block.payload.items[0].category).toBe('财报');
      expect(block.payload.items[0].verdict).toBe('超预期');
    }
  });

  it('degrades an empty checkpoints items array to unsupported', () => {
    const report = readTrackReportPayload({
      summary: 'Invalid',
      blocks: [{ id: 'b_bad', kind: 'checkpoints', payload: { items: [] } }],
    });
    expect(report?.blocks?.[0]).toEqual({ id: 'b_bad', kind: 'unsupported', declaredKind: 'checkpoints' });
  });
});
