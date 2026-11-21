/** @type {Record<string, string>} */
const GROUP_TITLES = {
  active: "当前优化 Loop",
  queued: "下一轮 Roadmap（按优先级）",
  closed: "已闭环验证",
  rolled_back: "已回滚实验",
};

/** @type {Record<string, number>} */
const STATE_ORDER = { active: 0, queued: 1, closed: 2, rolled_back: 3 };

/** @param {Record<string, any>} kr */
function formatUpdatedAt(kr) {
  if (kr.updatedAtDisplay) return kr.updatedAtDisplay;
  const iso = kr._meta && kr._meta.updatedAt ? String(kr._meta.updatedAt) : "";
  if (!iso) return "—";
  return iso.length >= 16 ? iso.slice(5, 16).replace("T", " ") : iso;
}

export const views = {
  /** @param {Record<string, any>} data */
  krLedger(data) {
    /** @type {any[]} */
    const krs = Array.isArray(data.krs) ? data.krs : [];
    return {
      metrics: krs.map((kr) => ({
        code: kr.code,
        label: kr.label,
        tag: kr.tag,
        current: kr.current,
        targetLabel: kr.targetLabel,
        status: kr.status,
        updatedAt: formatUpdatedAt(kr),
      })),
    };
  },

  /**
   * @param {Record<string, any>} data
   * @param {import("../perga-template.d.ts").KernelFactsSnapshot} kernel
   */
  loopRoadmap(data, kernel) {
    const tasks = kernel && kernel.tasks ? kernel.tasks : {};
    /** @type {any[]} */
    const loops = Array.isArray(data.loops) ? data.loops.slice() : [];
    loops.sort((a, b) => (STATE_ORDER[a.state] ?? 9) - (STATE_ORDER[b.state] ?? 9));

    return {
      items: loops.map((lp) => {
        const kTask = lp.taskRef ? tasks[lp.taskRef] : undefined;
        const fallbackStatus = lp.state === "active"
          ? "running"
          : lp.state === "queued"
            ? "pending"
            : lp.state === "rolled_back"
              ? "blocked"
              : "done";

        return {
          id: lp.id,
          group: GROUP_TITLES[lp.state] || lp.state,
          date: lp.priority || lp.date || "",
          title: lp.title,
          status: kTask ? kTask.status : (lp.status || fallbackStatus),
          verdict: lp.verdict,
          taskKey: lp.taskRef,
          sessionId: kTask ? kTask.sessionId : undefined,
          model: kTask ? kTask.model : lp.assign,
          assign: kTask && kTask.model ? kTask.model : lp.assign,
          assignKind: lp.assignKind === "cli" ? "command" : "model",
          triggerAt: lp.triggerAt,
          summary: lp.summary,
          expectations: lp.localExpects,
        };
      }),
    };
  },
};
