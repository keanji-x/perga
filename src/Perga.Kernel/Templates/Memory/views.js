/** @typedef {{ ref: string, title: string, summary: string, pinned: boolean, tags: string[], sources: string[], body: string, updatedAtMs: number }} MemoryEntry */

/** @param {number} bytes */
function formatBytes(bytes) {
  return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`;
}

/**
 * @param {import("../../../../templates/perga-template.d.ts").TemplateContext} ctx
 * @returns {import("../../../../templates/perga-template.d.ts").TemplateHeaderView}
 */
export function headerView({ data }) {
  const metrics = [
    { label: "Pinned", value: `${formatBytes(data.pinnedUsedBytes ?? 0)} / ${formatBytes(data.pinnedBudgetBytes ?? 0)}` },
    { label: "Entries", value: String(data.entries?.length ?? 0) },
  ];
  if (data.scope === "area")
    metrics.push({ label: "Global Inherited", value: String(data.globalEntryCount) });
  return { metrics };
}

export const views = {
  /** @param {{ entries?: MemoryEntry[] }} data */
  pinned(data) {
    return { kind: "pinned", entries: (data.entries ?? []).filter(entry => entry.pinned) };
  },
  /** @param {{ entries?: MemoryEntry[] }} data */
  topics(data) {
    return { kind: "topics", entries: (data.entries ?? []).filter(entry => !entry.pinned) };
  },
};
