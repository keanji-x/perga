/** @param {Record<string, any>} kr */
function formatUpdatedAt(kr) {
  if (kr.updatedAtDisplay) return kr.updatedAtDisplay;
  const iso = kr._meta && kr._meta.updatedAt ? String(kr._meta.updatedAt) : "";
  if (!iso) return "—";
  return iso.length >= 16 ? iso.slice(5, 16).replace("T", " ") : iso;
}

export const views = {
  /**
   * @param {Record<string, any>} data
   * @returns {import("../perga-template.d.ts").BuiltinTableView}
   */
  krLedger(data) {
    const krs = Array.isArray(data.krs) ? data.krs : [];
    return {
      title: "Key Results 总账",
      columns: [
        { key: "result", label: "Key Result" },
        { key: "current", label: "当前" },
        { key: "target", label: "目标" },
        { key: "updatedAt", label: "更新时间", align: "right" },
      ],
      rows: krs.map((kr) => ({
        result: { text: kr.label, code: kr.code, tag: kr.tag ?? undefined, markdown: true },
        current: {
          text: kr.current ?? "—",
          tone: kr.status === "met" ? "good" : kr.status === "on_track" ? "accent" : "warn",
          check: kr.status === "met",
        },
        target: kr.targetLabel,
        updatedAt: formatUpdatedAt(kr),
      })),
    };
  },
};
