/** @typedef {{ ref: string, title: string, summary: string, pinned: boolean, tags: string[], sources: string[], body: string, updatedAtMs: number }} MemoryEntry */

/** @param {number} bytes */
function formatBytes(bytes) {
  return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`;
}

/**
 * @param {import("../perga-template.d.ts").TemplateContext} ctx
 * @returns {import("../perga-template.d.ts").TemplateHeaderView}
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

/**
 * @param {MemoryEntry[]} entries
 * @param {"pinned" | "topics"} kind
 */
function entryView(entries, kind) {
  return {
    kind,
    entries: entries.filter(entry => entry.pinned === (kind === "pinned")).map(entry => {
      const { ref, title, summary, tags, sources, body, updatedAtMs } = entry;
      const opening = body.replace(/<!--[\s\S]*?(?:-->|$)/g, "").trimStart()
        .split(/\r\n?|\n/, 1)[0].replace(/\s+/g, " ");
      const normalized = summary.trim().replace(/\s+/g, " ");
      const repeatedSummary = opening.startsWith(normalized) || opening.replace(/^#{1,6}\s+/, "").startsWith(normalized);
      return { ref, title, summary, tags, sources, body, updatedAtMs, repeatedSummary };
    }),
  };
}

export const views = {
  /** @param {{ entries?: MemoryEntry[] }} data */
  pinned(data) {
    return entryView(data.entries ?? [], "pinned");
  },
  /** @param {{ entries?: MemoryEntry[] }} data */
  topics(data) {
    return entryView(data.entries ?? [], "topics");
  },
};
