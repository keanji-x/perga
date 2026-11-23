import type { FlowNodeProjection, FlowProjection } from '../../../../../core/domain/report-flow.ts';
import type { TemplateViewsProjection } from '../../../../../core/domain/report-widget.ts';
import type { TrackReport } from '../../../../../core/domain/report.ts';
import { NEUTRAL_ACTIVITY, type Track } from '../../../../../core/domain/track.ts';

export type ReportEmptyLeadPhase = 'idle' | 'running' | 'waiting_reply' | 'failed';

export type ReportEmptyLeadState = Readonly<{
  id: string;
  phase: ReportEmptyLeadPhase;
  modelLabel?: string | null;
  startedAtMs?: number | null;
}>;

const GUIDE_TEMPLATE_ID = '__perga_workspace_guide__';

function createFallbackTrack(): Track {
  return {
    id: 'guide',
    areaId: 'guide',
    title: '',
    sort: 0,
    cwd: '',
    agentCwd: '',
    isolation: 'none',
    pinnedAt: null,
    closedAt: null,
    createdAt: 0,
    updatedAt: 0,
    ...NEUTRAL_ACTIVITY,
  };
}

const GUIDE_RULES = Object.freeze([
  Object.freeze({
    label: 'Template 协议栏',
    text: '顶部这一栏本身就是 Perga 的 TemplateProtocolBar。套用真实模板后，这里会展示该模板的状态徽章、关键指标、规则与可调参数。',
  }),
  Object.freeze({
    label: 'Flow 编排图',
    text: '下方「起步流程」直接使用 Perga 原生 builtin:flow 组件渲染；点击任意节点可查看执行说明、依赖关系与运行状态。',
  }),
  Object.freeze({
    label: '自动退场',
    text: '这份引导仅在空白未套用模板时展示，不会写入数据库或污染模板目录；一旦套用模板或 Lead 落笔写入正式 Report，便会自动替换。',
  }),
]);

export const GUIDE_REPORT: TrackReport = Object.freeze({
  summary: 'Perga 空白工作区引导',
  body: '',
  blocks: Object.freeze([
    Object.freeze({
      id: 'guide-overview',
      kind: 'prose' as const,
      payload: Object.freeze({
        markdown: '# 进展会整理在这里\n\n你眼前这张页面本身就是由 **Perga** 的 **Template 协议栏 + Flow + Report** 共同渲染出来的。可以直接在右侧向 **Lead** 说出想做的事；明确目标后，它会自动拟定标题、按需套用模板，并将计划、研究与结论持续整理进本页。',
      }),
    }),
    Object.freeze({
      id: 'guide-flow',
      kind: 'widget' as const,
      payload: Object.freeze({
        use: 'builtin:flow',
        view: 'flow',
        title: '起步流程',
      }),
    }),
    Object.freeze({
      id: 'guide-surfaces',
      kind: 'prose' as const,
      payload: Object.freeze({
        markdown: '# 工作方式\n\n| 工作面 | 职责 |\n| --- | --- |\n| **Report（本页）** | 持续演进的活文档——计划、研究、判断与实时图表都呈现在纸面上，而不是埋在长对话里。 |\n| **Conversation** | 与 Lead 及子会话的工作对话流——用于提出目标、澄清需求、同步本页发生了什么变化。 |\n| **Template & Flow** | 可选的结构与编排——为投研、阶段交付或周期闭环提供专属视图、数据与日程，随时可挂可撤。 |',
      }),
    }),
    Object.freeze({
      id: 'guide-templates',
      kind: 'prose' as const,
      payload: Object.freeze({
        markdown: '# 从模板起稿\n\n如果这项工作适合固定结构，点选下方任一模板即可直接铺好初始报告、数据视图与 Flow；也可以保持空白，在对话中随时通过 `@:` 引用，或交给 Lead 自行判断。',
      }),
    }),
  ]),
});

export function buildGuideTrack(
  track: Track | undefined,
  lead: ReportEmptyLeadState | undefined,
  readyCount: number,
): Track {
  const phase = lead?.phase ?? 'idle';
  const primaryBadge = phase === 'running'
    ? { label: 'Lead 运行中', tone: 'accent' as const }
    : phase === 'waiting_reply'
      ? { label: '等待你的确认', tone: 'warn' as const }
      : phase === 'failed'
        ? { label: 'Lead 需要介入', tone: 'error' as const }
        : { label: '缺省引导', tone: 'neutral' as const };

  return {
    ...(track ?? createFallbackTrack()),
    templateId: GUIDE_TEMPLATE_ID,
    templateTitle: 'Perga 工作区引导',
    templateHeaderView: {
      badges: [
        primaryBadge,
        { label: `${readyCount} 份模板可用`, tone: 'neutral' },
      ],
      rules: [...GUIDE_RULES],
    },
  };
}

function buildLeadScopeNode(lead: ReportEmptyLeadState | undefined): FlowNodeProjection {
  const phase = lead?.phase ?? 'idle';
  const status = phase === 'running'
    ? 'running'
    : phase === 'failed'
      ? 'failed'
      : 'waiting';
  const prompt = phase === 'running'
    ? 'Lead 正在右侧对话中梳理你的目标、判断是否需要套用模板，并准备将成果整理进本页。'
    : phase === 'waiting_reply'
      ? 'Lead 已在右侧回应并等待你的确认；你也可以不等对话，直接在下方点选模板切换。'
      : '在右侧对 Lead 说出想做的事即可开始；也可以直接在下方点选模板，立刻进入结构化工作区。';

  return {
    id: 'guide-lead-scope',
    title: '明确目标与范围',
    source: 'template',
    whenKind: 'immediate',
    after: [],
    doKind: 'wake',
    role: 'Lead 对话',
    targetSession: 'lead',
    model: lead?.modelLabel ?? 'Lead',
    prompt: phase === 'failed' ? null : prompt,
    canceled: false,
    status,
    outcome: phase === 'failed' ? 'attention' : 'none',
    outcomeSummary: phase === 'failed' ? 'Lead 本轮运行中断，请点击上方 Lead 进入右侧对话查看原因或继续。' : null,
    runCount: phase === 'running' || phase === 'waiting_reply' || phase === 'failed' ? 1 : 0,
    sessionId: lead?.id ?? null,
    startedAtMs: phase === 'running' ? (lead?.startedAtMs ?? null) : null,
    finishedAtMs: null,
    nextFireAtMs: null,
    lastFiredAtMs: null,
    firedCount: 0,
    waitingOn: [],
    gates: [],
    watchProgress: null,
    repeat: false,
  };
}

export function buildGuideViews(lead: ReportEmptyLeadState | undefined): TemplateViewsProjection {
  const leadNode = buildLeadScopeNode(lead);
  const templateNode: FlowNodeProjection = {
    id: 'guide-choose-template',
    title: '切换模板或保持空白',
    source: 'template',
    whenKind: 'after',
    after: [{ node: leadNode.id, on: 'ok' }],
    doKind: 'run',
    role: 'Template',
    targetSession: null,
    model: null,
    prompt: '当任务有固定结构时，Lead 会自动套用最匹配的模板并铺好视图与 Flow；你也可以随时在下方点选模板一键切换，或在对话中用 @: 指定。',
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
    waitingOn: [leadNode.id],
    gates: [],
    watchProgress: null,
    repeat: false,
  };
  const deliverNode: FlowNodeProjection = {
    id: 'guide-living-report',
    title: '沉淀正式 Report 与编排',
    source: 'template',
    whenKind: 'after',
    after: [{ node: templateNode.id, on: 'ok' }],
    doKind: 'run',
    role: 'Report & Flow',
    targetSession: null,
    model: null,
    prompt: '一旦套用模板或 Lead 落笔写入正式内容，这份缺省引导会自动退场，替换为你的正式 Report、实时数据视图与真实 Flow。',
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
    waitingOn: [templateNode.id],
    gates: [],
    watchProgress: null,
    repeat: false,
  };

  const flow: FlowProjection = {
    rev: 0,
    filePath: null,
    nodes: [leadNode, templateNode, deliverNode],
    archived: [],
  };

  return {
    templateId: GUIDE_TEMPLATE_ID,
    views: {},
    widgets: {},
    dataRev: 0,
    flowRev: 0,
    flow,
    diagnostics: [],
  };
}
