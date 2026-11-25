import { useMemo } from 'react';
import type { FlowNodeProjection } from '../../../../../core/domain/report-flow.ts';
import type { TemplateViewsProjection } from '../../../../../core/domain/report-widget.ts';
import type { TrackReport } from '../../../../../core/domain/report.ts';
import { ReportDocument } from '../document/public.tsx';
import styles from './empty.module.css';

type SampleFlowNodeInput = Pick<FlowNodeProjection, 'id' | 'title' | 'whenKind' | 'doKind' | 'status'>
  & Partial<Pick<FlowNodeProjection, 'after' | 'role' | 'prompt' | 'outcome' | 'outcomeSummary' | 'runCount' | 'waitingOn'>>;

function sampleFlowNode(input: SampleFlowNodeInput): FlowNodeProjection {
  return {
    source: 'agent',
    after: [],
    role: null,
    targetSession: null,
    model: null,
    prompt: null,
    canceled: false,
    outcome: 'none',
    outcomeSummary: null,
    runCount: 0,
    sessionId: null,
    startedAtMs: null,
    finishedAtMs: null,
    nextFireAtMs: null,
    lastFiredAtMs: null,
    firedCount: 0,
    waitingOn: [],
    gates: [],
    watchProgress: null,
    repeat: false,
    ...input,
  };
}

function createBlankGuideReport(): TrackReport {
  return {
    summary: 'Perga empty workspace guide',
    body: '',
    blocks: [
      {
        id: 'empty-guide-intro',
        kind: 'prose',
        payload: {
          markdown: [
            '# Progress lives here',
            '',
            'Tell **Lead** what you want to work on in Conversation to get started. Once the goal is clear, it will title the track, attach a template if helpful, and keep outcomes organized on this page.',
            '',
            '- **Report (this page)** — A living document where plans, research, decisions, live views, and workspace file references stay readable instead of buried in chat.',
            '- **Conversation** — Your working thread with Lead and child sessions for framing goals, clarifying requirements, and tracking updates to this page.',
            '- **Template & Flow** — Optional structure and orchestration; attach a template when a workflow fits (or mention one with `@:`), and ask Lead anytime to adjust or cancel Flow steps.',
          ].join('\n'),
        },
      },
      {
        id: 'empty-guide-flow-intro',
        kind: 'prose',
        payload: {
          markdown: [
            '# Sample Flow: how multi-step work runs',
            '',
            'When a task benefits from staged execution, independent review, or scheduled checks, Lead declares dependencies in **Flow**. Click any sample node below to inspect its details; once real work begins, this guide is replaced automatically.',
          ].join('\n'),
        },
      },
      {
        id: 'empty-guide-flow-sample',
        kind: 'widget',
        payload: {
          use: 'builtin:flow',
          view: 'flow',
          title: 'Sample Flow',
        },
      },
    ],
  };
}

function createSampleFlowViews(): TemplateViewsProjection {
  return {
    templateId: '',
    views: {},
    widgets: {},
    dataRev: 0,
    flowRev: 0,
    flow: {
      rev: 0,
      filePath: null,
      nodes: [
        sampleFlowNode({
          id: 'sample-scope',
          title: 'Scope goal and plan',
          whenKind: 'immediate',
          doKind: 'wake',
          role: 'Lead',
          status: 'done',
          outcome: 'ok',
          outcomeSummary: 'Example: Lead clarifies the goal, sets the title, and attaches a matching template if structured views or schedules fit.',
          runCount: 1,
        }),
        sampleFlowNode({
          id: 'sample-execute',
          title: 'Run research and review',
          whenKind: 'after',
          after: [{ node: 'sample-scope', on: 'ok' }],
          doKind: 'run',
          role: 'Worker',
          status: 'running',
          prompt: 'Example: Dispatches child sessions in dependency order for research, implementation, or independent review with automated checks.',
          runCount: 1,
        }),
        sampleFlowNode({
          id: 'sample-publish',
          title: 'Synthesize into Report',
          whenKind: 'after',
          after: [{ node: 'sample-execute', on: 'ok' }],
          doKind: 'wake',
          role: 'Lead',
          status: 'pending',
          prompt: 'Example: Wakes Lead once upstream steps settle to record findings, data, and follow-ups in this Report.',
          waitingOn: ['sample-execute'],
        }),
      ],
      archived: [],
    },
    diagnostics: [],
  };
}

export function ReportEmpty() {
  const report = useMemo(() => createBlankGuideReport(), []);
  const views = useMemo(() => createSampleFlowViews(), []);

  return <div className={styles.guide} data-nc-report-empty="">
    <ReportDocument
      report={report}
      templateViews={views}
      allowAutoFlow={false}
      authored={false}
      empty={null}
    />
  </div>;
}
