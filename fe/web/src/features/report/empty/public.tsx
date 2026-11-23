import { useMemo } from 'react';
import type { TemplateViewsProjection } from '../../../../../core/domain/report-widget.ts';
import type { TrackReport } from '../../../../../core/domain/report.ts';
import { ReportDocument } from '../document/public.tsx';
import styles from './empty.module.css';

function createBlankGuideReport(): TrackReport {
  return {
    summary: 'Perga 空白工作区引导',
    body: '',
    blocks: [
      {
        id: 'empty-guide-intro',
        kind: 'prose',
        payload: {
          markdown: [
            '# 进展会整理在这里',
            '',
            '在右侧向 **Lead** 说出想做的事即可开始。明确目标后，它会自动拟定标题、按需匹配模板，并将成果持续整理进本页。',
            '',
            '- **Report（本页）** — 持续演进的活文档，计划、研究、判断与实时视图都沉淀在纸面上，而非埋在长对话里。',
            '- **Conversation** — 与 Lead 及子会话的协作流，用于提出目标、澄清需求、同步本页发生了什么变化。',
            '- **Template & Flow** — 可选的结构与编排；任务适合固定结构时按需挂载，也可随时通过 `@:` 引用或撤销。',
          ].join('\n'),
        },
      },
      {
        id: 'empty-guide-flow-intro',
        kind: 'prose',
        payload: {
          markdown: [
            '# Flow 示例：多步工作如何自动流转',
            '',
            '当任务需要拆步推进、独立复核或周期巡检时，Lead 会用 **Flow** 声明依赖与触发条件。下面是一份静态示例，点选节点即可查看详情；正式动工后，本页会自动替换为你的真实 Report 与 Flow。',
          ].join('\n'),
        },
      },
      {
        id: 'empty-guide-flow-sample',
        kind: 'widget',
        payload: {
          use: 'builtin:flow',
          view: 'flow',
          title: '流程示例',
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
        {
          id: 'sample-scope',
          title: '明确目标与范围',
          source: 'agent',
          whenKind: 'immediate',
          after: [],
          doKind: 'wake',
          role: 'Lead',
          targetSession: null,
          model: null,
          prompt: null,
          canceled: false,
          status: 'done',
          outcome: 'ok',
          outcomeSummary: '示例：Lead 梳理目标并拟定标题；若任务适合固定结构，会在动工前自动套用合适模板铺好视图与日程。',
          runCount: 1,
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
        },
        {
          id: 'sample-execute',
          title: '拆分调研与独立复核',
          source: 'agent',
          whenKind: 'after',
          after: [{ node: 'sample-scope', on: 'ok' }],
          doKind: 'run',
          role: 'Worker',
          targetSession: null,
          model: null,
          prompt: '示例：按前置依赖自动派发子会话执行调研、编码或独立审查，并可附加自动化测试与门禁校验。',
          canceled: false,
          status: 'running',
          outcome: 'none',
          outcomeSummary: null,
          runCount: 1,
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
        },
        {
          id: 'sample-publish',
          title: '汇总沉淀进 Report',
          source: 'agent',
          whenKind: 'after',
          after: [{ node: 'sample-execute', on: 'ok' }],
          doKind: 'wake',
          role: 'Lead',
          targetSession: null,
          model: null,
          prompt: '示例：上游步骤完成后自动唤醒 Lead，将最新结论、数据与后续定时复核整理进本页 Report。',
          canceled: false,
          status: 'pending',
          outcome: 'none',
          outcomeSummary: null,
          runCount: 0,
          sessionId: null,
          startedAtMs: null,
          finishedAtMs: null,
          nextFireAtMs: null,
          lastFiredAtMs: null,
          firedCount: 0,
          waitingOn: ['sample-execute'],
          gates: [],
          watchProgress: null,
          repeat: false,
        },
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
      empty={null}
    />
  </div>;
}
