/** @param {import("../perga-template.d.ts").SchemaBuilder} t */
export const schema = (t) => ({
  scope: t.enum(["area", "global"]),
  areaId: t.string().optional(),
  pinnedBudgetBytes: t.number(),
  pinnedUsedBytes: t.number(),
  globalEntryCount: t.number(),
  entries: t.array(t.object({
    ref: t.string(),
    key: t.string(),
    title: t.string(),
    summary: t.string(),
    pinned: t.boolean(),
    tags: t.array(t.string()),
    sources: t.array(t.string()),
    body: t.markdown(),
    sizeBytes: t.number(),
    updatedAtMs: t.number(),
    etag: t.string(),
  })),
});

export const initial = () => ({
  scope: "global",
  pinnedBudgetBytes: 0,
  pinnedUsedBytes: 0,
  globalEntryCount: 0,
  entries: [],
});
