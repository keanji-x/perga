import { describe, expect, it } from 'vitest';
import {
  flowCommitResultSchema,
  flowProjectionSchema,
} from './report-flow.js';
import { templateViewsProjectionSchema } from './report-widget.js';

describe('report-flow domain contracts', () => {
  it('parses FlowProjection with defaults, watchProgress, gates, and archived history', () => {
    const parsed = flowProjectionSchema.parse({
      rev: 3,
      filePath: '/data/tracks/trk_1/flow.json',
      nodes: [
        {
          id: 'fix-r2',
          title: '修复 R1 拦截项并推送',
          status: 'done',
          outcome: 'ok',
          outcomeSummary: '全部测试通过并已推送。',
          runCount: 1,
          sessionId: 'ses_impl_1',
          model: 'claude-opus-4-6',
          gates: [
            { label: 'dotnet test', status: 'pass', command: 'dotnet test Perga.sln', output: 'Passed: 120' },
          ],
        },
        {
          id: 'wait-ci',
          title: '等待 PR #115 CI',
          whenKind: 'after',
          after: [{ node: 'fix-r2' }],
          doKind: 'watch',
          status: 'waiting',
          watchProgress: {
            probeKind: 'prChecks',
            label: 'PR #115',
            passedChecks: 3,
            totalChecks: 5,
            lastCheckedAtMs: 1_760_000_000_000,
          },
        },
      ],
      archived: [
        {
          id: 'r1-review',
          title: '首轮评审',
          outcome: 'ok',
          outcomeSummary: '提出 2 项拦截项。',
          sessionId: 'ses_reviewer_a',
          model: 'gpt-5.3-codex',
          finishedAtMs: 1_759_999_000_000,
          archivedAtMs: 1_759_999_500_000,
        },
      ],
    });

    expect(parsed.rev).toBe(3);
    expect(parsed.nodes).toHaveLength(2);
    expect(parsed.nodes[0].source).toBe('agent');
    expect(parsed.nodes[0].whenKind).toBe('immediate');
    expect(parsed.nodes[0].doKind).toBe('run');
    expect(parsed.nodes[0].repeat).toBe(false);
    expect(parsed.nodes[1].after).toEqual([{ node: 'fix-r2', on: 'ok' }]);
    expect(parsed.nodes[1].watchProgress?.passedChecks).toBe(3);
    expect(parsed.archived).toHaveLength(1);
    expect(parsed.archived[0].status).toBe('done');
  });

  it('decodes a repeating watch without a deadline and an explicit worker effort', () => {
    const parsed = flowProjectionSchema.parse({
      rev: 1,
      nodes: [
        {
          id: 'poll',
          title: 'Poll',
          doKind: 'watch',
          repeat: true,
          watchProgress: { probeKind: 'cmd', label: 'Poll', timeoutAtMs: null },
        },
        { id: 'review', title: 'Review', reasoningEffort: 'high', permissionMode: 'full' },
      ],
    });
    expect(parsed.nodes[0].repeat).toBe(true);
    expect(parsed.nodes[0].watchProgress?.timeoutAtMs).toBeNull();
    expect(parsed.nodes[1].reasoningEffort).toBe('high');
    expect(parsed.nodes[1].permissionMode).toBe('full');
  });

  it('parses FlowCommitResult including plan diff and validation diagnostics', () => {
    const result = flowCommitResultSchema.parse({
      ok: false,
      dryRun: true,
      flowRev: 4,
      plan: {
        added: ['recheck-a'],
        updated: ['wait-ci'],
        canceled: ['old-node'],
        archived: [],
        unchanged: ['fix-r2'],
      },
      diagnostics: [
        {
          path: 'nodes[2].after',
          message: 'Active node cannot be silently deleted',
          suggestion: 'Set canceled: true explicitly',
        },
      ],
      flow: {
        rev: 4,
        nodes: [],
        archived: [],
      },
    });

    expect(result.ok).toBe(false);
    expect(result.dryRun).toBe(true);
    expect(result.flowRev).toBe(4);
    expect(result.plan.canceled).toEqual(['old-node']);
    expect(result.diagnostics).toHaveLength(1);
    expect(result.diagnostics[0].suggestion).toContain('canceled: true');
  });

  it('parses TemplateViewsProjection carrying flowRev and flow for template-less tracks', () => {
    const projection = templateViewsProjectionSchema.parse({
      flowRev: 2,
      flow: {
        rev: 2,
        nodes: [
          { id: 'daily-ci', title: '每日 CI 巡检', whenKind: 'cron', cron: '0 9 * * *', firedCount: 5 },
        ],
      },
    });

    expect(projection.templateId).toBe('');
    expect(projection.dataRev).toBe(0);
    expect(projection.flowRev).toBe(2);
    expect(projection.flow?.nodes[0].cron).toBe('0 9 * * *');
    expect(projection.flow?.nodes[0].firedCount).toBe(5);
  });
});
