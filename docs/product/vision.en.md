# Vision and Fundamental Tensions

## Core Premise

> **Assuming an individual AI is already as capable as a human expert, how should we organize multiple AIs to execute long-horizon work — maintaining a codebase, running ongoing investment research, or driving multi-week engineering metrics — continuously and effectively?**

This is Perga's first premise. Unlike earlier tools built to compensate for weak models with rigid static flowcharts, Perga assumes individual model intelligence will continue approaching and surpassing human experts, and instead confronts a more durable question: **even when every participant is brilliant, which fundamental tensions remain unavoidable when coordinating multiple actors across days, weeks, and months?**

## Four Fundamental Tensions

These four tensions sit on orthogonal axes — **time, collaboration, objectives, and evolution** — and do not disappear as base models improve:

1. **Time: Bounded Context vs. Unbounded Task Horizon**
   Every individual actor has a finite working memory and effective attention window, while long-horizon work (an evolving repository, a multi-quarter research thesis) grows without bound in history, decisions, and state. Stuffing everything into context causes attention rot; naive truncation or summarization destroys causal chains and hard-won lessons.

2. **Collaboration: Parallel Scale vs. Bandwidth & Handoff Entropy**
   A single actor has limited throughput and specialization. Complex long-running work must fan out across parallel agents, relay across time, and stay aligned with the human principal. Yet every handoff (AI↔AI, AI↔human) passes through a **narrow, lossy channel** — introducing semantic drift, synchronization overhead, and workspace collisions. Humans are the narrowest bandwidth in the entire system: expose them directly to raw execution streams, and the organization immediately overloads.

3. **Objectives: Local Verifiability vs. Long-Term Intent Drift**
   Autonomous progress requires crisp, objective, immediately checkable local criteria (tests pass, latency drops, filings verified). Yet true long-term goals are open-ended, multi-dimensional, and delayed in feedback (three-year codebase maintainability, whether a core investment thesis has suffered a paradigm shift). Without local criteria, execution diverges; over-relying on local proxies invites Goodhart drift, reward hacking, and confirmation bias — every local light green while drifting away from real intent.

4. **Evolution: Institutional Convergence vs. Adaptability to the Unknown**
   To solve recurring problems faster and more reliably, organizations naturally accumulate processes, rules, specialization, and guardrails. Yet overfitting to known problems breeds path dependence and rigidity — when conditions shift or novel work arrives, heavy process becomes the main obstacle to exploration.

## Why Not Copy Human Companies: Four Physical Asymmetries

Human societies have wrestled with these same four tensions for millennia, producing fixed departments, group chats, meetings, and employee handbooks. AI differs physically on four dimensions, so an AI-native organization should not be a digital replica of a human company:

1. **Near-zero spin-up and teardown cost.** Hiring and transferring humans is expensive, forcing permanent departments and static titles. AIs can be spawned per task, mixed across providers, and retired on completion — **the organizational unit should be the dynamic task, not a fixed role.**
2. **Zero ego friction, zero biological memory.** Humans resist abrupt interruption, replacement, or transparent audits, yet carry continuous memory across months. AIs are the exact opposite: they can be transparently reviewed, steered mid-flight, or rolled back instantly, yet lose memory across sessions — **organizational memory must be externalized into durable artifacts, never trapped inside one context window.**
3. **100× execution speed mismatch.** A group of AIs can emit tens of thousands of lines of logs and dozens of edits in an hour; human chat-stream coordination collapses immediately — **the human interface must shift from scrolling chat to distilled read-only reports and exception escalation.**
4. **Governance can finally be physically enforced.** Human company policies live in handbooks enforced by social norms and post-hoc blame. Every AI tool call, model route, and completion passes through software — **checks and balances can be written as deterministic, unbypassable kernel code.**

## Long-Term Vision

Perga explores a **Task-Driven Intelligent Organization** — designed around the physical nature of AI to find an elegant, dynamic balance across these four tensions.

To stay honest: **these tensions and vision are a north star, not a claim of solved perfection.** Today's Perga thinks big on vision and small on design — solving the sharpest concrete bottlenecks on a single multi-day Track first, one disciplined step at a time.

---

- Further reading: [README](../../README.md) · [Architecture Reference](../../DESIGN.md) · [中文原文 (vision.md)](vision.md)
