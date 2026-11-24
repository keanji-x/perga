import { schema, initial } from "./schema.js";
import { headerView, views } from "./views.js";

export default defineTemplate({
  id: "memory",
  title: "Memory",
  description: "Curate durable facts and preferences with the Memory Planner.",
  data: { schema, initial },
  headerView,
  report: {
    template: "./report.md",
    views,
  },
  before(action) {
    return action.type === "report.commit"
      ? Deny("The Memory report is generated from Markdown entries. Curate entries with memory_put and memory_delete.")
      : Allow();
  },
});
