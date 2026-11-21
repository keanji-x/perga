/**
 * @param {Record<string, any>} inputs
 * @returns {import("../perga-template.d.ts").TriggerSpec[]}
 */
export function createTriggers(inputs) {
  return [
    {
      id: "weekly-price-refresh",
      label: `每周一 08:30 刷新 ${inputs.ticker} 现价与同行估值快照`,
      on: { kind: "cron", expr: "30 8 * * 1", timezone: "America/New_York" },
      action: {
        kind: "run_cli",
        command: `python scripts/equity_snapshot.py --ticker ${inputs.ticker}`,
        writeDataPath: "valuation.currentPrice",
      },
    },
    {
      id: "price-shock-triage",
      label: "单日涨跌幅 ≥ 8% 时拉起独立大事件排查",
      on: { kind: "data_change", path: "valuation.currentPrice" },
      when: ({ event }) =>
        typeof event.prev === "number" &&
        event.prev > 0 &&
        Math.abs(((event.next - event.prev) / event.prev) * 100) >= 8,
      action: {
        kind: "dispatch_worker",
        role: "event_review",
        goal: ({ event }) => {
          const pct = (((event.next - event.prev) / event.prev) * 100).toFixed(1);
          return `${inputs.ticker} 单日异动 ${pct}%：动态新增事件条目，核查核心证伪红线并归档快评。`;
        },
      },
    },
    {
      id: "settle-due-event",
      label: "预挂财报/大事件到期后自动按事前 Expect 核查兑现",
      on: { kind: "schedule_at", dataPath: "events", dateField: "date" },
      when: ({ event }) => event.record?.verdict === "pending",
      action: {
        kind: "dispatch_worker",
        role: "earnings",
        goal: ({ event }) =>
          `结算事件 '${event.record.title}'：抓取原始公告，逐条核对事前 expects 并更新 verdict。`,
      },
    },
  ];
}
