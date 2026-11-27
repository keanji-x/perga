# Stop Scrolling Chat Logs: Why I Built a Report-Centric Agent Harness

I'm done with those disorienting mornings: sitting down at my desk and staring at a dozen TUI windows left over from last night—one crashed halfway through, another buried under hundreds of screens of tool calls, and a few I can't even remember opening. Maybe human attention really does have a ceiling.

I want a proper boss morning: flipping through crisp briefs and charts prepared by my team (unless you're Jensen Huang, happily reading a hundred emails a day) and setting priorities for the day. Why are we still putting up with endless, append-only chat threads?

So I built [Perga](https://github.com/keanji-x/perga)—a report-centric rather than chat-centric agent harness.

## Why report-centric

The medium between you and your agents has to be bounded. Models can generate forever; human working memory can't keep up with an unbounded chat stream.

Keeping that medium bounded also sets your ceiling on concurrency. Attention is a fixed budget: the less it takes to catch up on one agent, the more agents you can run in parallel.

I call this bounded medium a **report**. Humans and AI collaborating around a continuously updated report rather than an endless scroll of messages—that's what **report-centric** means.

## What makes a good report

A good report has to get two things right: **readability** and **maintainability**. **Spec-driven coding** tried to solve the same problem—replacing chat with a shared Markdown file—and failed on both counts.

**What's easy for humans to read and what's easy for AI to work with pull in opposite directions.** Humans want compressed takeaways and visuals so they can grasp the state in seconds, steer, and decide. AI wants low-noise, structured facts that software can actually validate.

Plain prose tries to serve both and serves neither. For humans, specs bloat into boilerplate nobody reads. For AI, Markdown TODOs are unverifiable promises—models skip steps and check boxes as pure theater.

**Readability isn't static; maintainability is what keeps a report readable.** A report has to be kept in sync continuously by humans and AI together, so it needs clean partial edits for the model, an easy way for humans to spot drift, and real version history.

Anyone who has maintained a `SPEC.md` with an agent knows that moment of quiet fury: it dumps a pile of edge cases at the bottom, quietly breaks a clean design decision three sections up, and leaves behind a tangle of convoluted prose. After a few rounds, the file is beyond saving, and nobody opens it again.

## Building the underlying tooling

To make **readability** and **maintainability** work in practice, Perga relies on three primitives:

**Separate data from presentation.** Task and domain state live as schema-validated structured data—exact, verifiable facts for AI to read and write. On top of it, Perga layers frontend widgets—tables, progress views, or custom charts—so humans can take in the state at a glance. When the underlying state changes, the views update automatically, and tweaking the presentation never corrupts the ground truth.

**Version every block.** We can't stop AI from making bad edits, but we can at least undo them. Perga splits each report into independent blocks, recording every edit as a commit with full revision history.

**Run multiple conversations around one report, separating execution from maintenance.** You can spin up a fresh conversation at any time to read the underlying data, walk you through what happened, get back on the same page, or tidy up the report—without polluting the context of the agent doing the actual work.

Since switching to Perga, my mornings at the desk have gotten much quieter. Every now and then, I'll ask an agent to tweak a report—cut a useless paragraph, try a cleaner layout, nudge it closer to how I like to read. In an era of recursive self-improvement, I guess it's my turn to report my taste back to the AI.
