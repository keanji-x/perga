# Why I Rewrote a 966k-Line AI-Built Rust Codebase in C#

I like types, and used to like Rust. Back in the classical era, I loved tinkering with enums and traits for the safety and control they gave me. Naturally, I carried Rust into the vibe-coding era for my agent orchestrator ([neige-calm](https://github.com/keanji-x/neige-calm)). Five months later, I scrapped the backend and rewrote it in C# to escape Rust, the devourer of time and disk.

At first, vibe coding in Rust felt like magic. Opus and GPT both wrote code and reviewed each other's diffs until tests passed. While the repo was small, I thought I had both Rust's safety and AI's speed—merging over 2,100 PRs, peaking at over 200 PRs a week.

Underneath that speed, the codebase quietly grew nearly 5x in two months, hitting 966k lines of Rust across 28 crates.

Then one afternoon, I kicked off a routine feature. A full day later, Opus and GPT were still trading diffs: fix, recompile, fail, fix again. Disk alerts fired, CPU stayed pegged, and spinning up a second `git worktree` choked the box.

---

## Why it stalled

At human speed, Rust's build tax is fine. At agent velocity, four things compounded into a dead stop:

1. **Over-defensive code fed the review loop.** Coding agents—especially GPT—cannot resist adding redundant fallbacks and speculative guards for imaginary edge cases. Every extra branch gave the next review new corner cases to flag, so each fix spawned more code and more reviews.
2. **Compile, link, and flaky-test retries multiplied every turn.** Across nearly a million lines of Rust, `rustc` and linking stretched a normal `write -> review -> fix` loop into hours. Worse, AI-written tests are often flaky, so one spurious failure meant another brutal compile and CI run. When every iteration costs hours, nobody rips out a bad design; you patch on top of it.
3. **Parallel `git worktree`s blew up `target/`.** Every worktree wanted tens of gigabytes, choking disk, SSD I/O, and CPU. We tried every Rust trick in the book: `sccache` filled up past 10 GB and fought `CARGO_INCREMENTAL`, `mold` couldn't save `rustc`, and sharing `CARGO_TARGET_DIR` just traded disk bloat for lock contention and constant rebuilds. We even turned off incremental builds and stripped debuginfo in `Cargo.toml` just to keep the disk alive—making builds even slower.
4. **Bad code became too expensive to delete.** Agents will write slop. Once it hardened across lifetimes, async traits, and dozens of crates, tearing it out cost too much compile and edit churn, so the repo only grew.

Yes, letting agents bloat a repo to 966k lines was partly on us. Still, is Rust innocent? Even a 3,300-line Rust terminal bridge produces an **805 MB** `target/debug` directory on its own.

---

## Picking a vibe-coding-friendly backend

What I needed was simple: fast builds, tiny artifacts, real types, solid speed, and low memory.

Everyone recommended Go. Its only flaw was that `if err != nil` doesn't feel like Rust.

Then I landed on **C# / .NET**. I enjoy its first-class `async`/`await`, strict nullability, pattern matching, fast Roslyn builds, and modest memory. With zero build-cache tuning, our entire ~105k-line C# backend builds into **~300–420 MB**—half the footprint of that tiny Rust bridge.

So I rewrote the orchestrator in C# ([Perga](https://github.com/keanji-x/perga), Apache 2.0). Rust stayed only where it is irreplaceable: our standalone PTY bridge (`perga-term-bridge`). Its `target/` is still nearly double our 460 MB `node_modules`—the biggest disk hog in every worktree. I only live with it because we barely touch it.

---

## Reversibility and three workflow rules

In nine days, the rewrite reached full feature parity—minus five months of dead experiments and defensive bloat. What keeps it small is the feedback loop: light build artifacts make parallel `git worktree`s effortless again, and seconds-long compile-test cycles restore **reversibility**. When an agent writes ugly code now, the fastest move is to delete it.

Language alone wasn't enough, so I locked in three workflow rules:

1. **Kill any `review -> fix` loop after three rounds.** Still trading punches after three passes? Stop patching and rethink the structure with a fresh model or your own eyes. Recently, an installer script bloated to 877 lines of Bash across six review rounds; scrapping the PR and moving those subcommands into C# cut it to 274 lines in one pass.
2. **Fix model roles, and keep GPT out of production code.** GPT is ruthless as a read-only reviewer, but when it writes code directly, its over-defensiveness and redundant fallbacks rot the repo. Having another model implement while GPT audits read-only—and explicitly hunting speculative guards—beat every prompt tweak I tried.
3. **Rewrite instead of patching.** Rewriting from scratch used to be taboo. With coding agents, once you actually understand the domain, starting clean is often faster—and far cleaner—than asking models to excavate months of accumulated slop.

I still miss those quiet classical-era afternoons, polishing a Rust type until nothing wrong could compile. When agents write thousands of lines a week, though, safety no longer comes from making bad code hard to compile once. It comes from making bad code cheap to delete.
