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
};
