import { useCallback, useState } from 'react';
import { ReportFlowBlock } from '../../fe/web/src/features/report/flow/public.tsx';
import { EditorialTable } from '../../fe/web/src/features/report/editorial/table.tsx';
import { EditorialChronicle } from '../../fe/web/src/features/report/editorial/chronicle.tsx';
import { WidgetFrame } from './WidgetFrame.tsx';
import {
  DEV_DELIVERY_FLOW,
  EQUITY_CHRONICLE_ITEMS,
  EQUITY_FLOW,
  EQUITY_VALUATION_AXIS,
  MEMORY_PINNED_DATA,
  MEMORY_TOPICS_DATA,
  OKR_FLOW,
  OKR_LEDGER_TABLE,
} from './showcaseData.ts';

type ProtocolTab = 'delivery' | 'equity' | 'okr' | 'memory';

const INSTALL_COMMAND = 'curl -fsSL https://keanji-x.github.io/perga/install.sh | sh';

const PROTOCOL_META: Readonly<Record<ProtocolTab, Readonly<{
  tabLabel: string;
  path: string;
  chip: string;
  protocolFile: string;
  datumLeft: string;
  datumRight: string;
  marginCap: string;
  marginTag: string;
}>>> = {
  delivery: {
    tabLabel: '01 agents-delivery',
    path: 'perga / core / track · auth-session-hardening',
    chip: 'OVERLAYFS · REV 14 · GATE EXIT 0',
    protocolFile: 'templates/agents-delivery/template.perga.js',
    datumLeft: 'PROTOCOL · AGENTS-DELIVERY (28 LINES)',
    datumRight: 'KERNEL-VERIFIED FLOW · CLICK ANY NODE',
    marginCap: 'SHARED WORKER STREAM',
    marginTag: 'LIVE STEER',
  },
  equity: {
    tabLabel: '02 equity-research',
    path: 'perga / research / track · NVDA-long-horizon',
    chip: 'WEEKLY CRON · ENFORCED SECOND-MODEL REVIEW',
    protocolFile: 'templates/equity-research/template.perga.js',
    datumLeft: 'PROTOCOL · EQUITY-RESEARCH (NVDA)',
    datumRight: 'HOVER PRICE TICKS · EXPAND EVENTS',
    marginCap: 'INDEPENDENT REVIEW',
    marginTag: 'ANTI-HINDSIGHT',
  },
  okr: {
    tabLabel: '03 okr-loop',
    path: 'perga / eng / track · q4-latency-and-reliability',
    chip: 'DAILY 0-TOKEN PROBE · KR LEDGER',
    protocolFile: 'templates/okr-loop/template.perga.js',
    datumLeft: 'PROTOCOL · OKR-LOOP (34 LINES)',
    datumRight: 'KERNEL-STAMPED KR UPDATES',
    marginCap: 'DAILY TRIAGE LOOP',
    marginTag: 'CRON + GATE',
  },
  memory: {
    tabLabel: '04 shared-memory',
    path: 'perga / core / memory · area-planner',
    chip: '742 / 2,048 B PINNED · ETAG LOCKED',
    protocolFile: 'src/Perga.Kernel/Templates/Memory/widgets/entries.html',
    datumLeft: 'GOVERNED HUMAN-AI MEMORY',
    datumRight: 'PLAIN UTF-8 MARKDOWN · EXPAND NOTES',
    marginCap: 'MEMORY PLANNER',
    marginTag: 'PROPOSE ≠ PUT',
  },
};

export function App() {
  const [activeTab, setActiveTab] = useState<ProtocolTab>('delivery');
  const [copied, setCopied] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const showNotice = useCallback((text: string) => {
    setNotice(text);
    window.setTimeout(() => {
      setNotice((current) => (current === text ? null : current));
    }, 3200);
  }, []);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(INSTALL_COMMAND);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1600);
    } catch {
      showNotice('Copy failed — select the command text directly.');
    }
  };

  const handleWidgetIntent = useCallback((intent: Record<string, unknown>) => {
    if (intent.kind === 'open_file' && typeof intent.path === 'string') {
      showNotice(`In Perga, this opens the local workspace file: ${intent.path}`);
    } else if (intent.kind === 'open_track' && typeof intent.trackId === 'string') {
      showNotice(`In Perga, this jumps directly to source Track ${intent.trackId}`);
    }
  }, [showNotice]);

  const meta = PROTOCOL_META[activeTab];

  return (
    <div className="site-frame">
      <header className="site-masthead">
        <div className="site-masthead-inner">
          <a className="site-wordmark" href="./" aria-label="Perga home">
            <span className="site-seal" aria-hidden="true">P</span>
            <span className="site-wordmark-text">Perga</span>
            <span className="site-wordmark-sub">LOCAL AGENT WORKSPACE</span>
          </a>
          <nav className="site-nav" aria-label="Primary">
            <a href="#specimen">Live Components</a>
            <a href="#thesis">Principles</a>
            <a href="#specs">Specs</a>
            <a href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.md" target="_blank" rel="noreferrer">Vision</a>
            <a className="site-nav-repo" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub ↗</a>
            {/* DISCORD SLOT: Uncomment when your 4-channel Discord invite link is ready
            <a className="site-nav-repo" href="https://discord.gg/YOUR_INVITE" target="_blank" rel="noreferrer">Discord ↗</a>
            */}
          </nav>
        </div>
      </header>

      <main>
        {/* HERO */}
        <section className="site-hero">
          <div className="site-hero-grid">
            <div className="site-hero-lead">
              <div className="site-kicker">
                <span className="site-kicker-dot" aria-hidden="true" />
                <span>OPEN SOURCE · APACHE 2.0 · MACOS ARM64 &amp; LINUX X64</span>
              </div>
              <h1 className="site-display">
                Chat drifts.<br />
                <em>Reports stay.</em>
              </h1>
              <p className="site-deck">
                A local workspace that moves multi-day <strong>Claude Code</strong> and <strong>Codex</strong> work out of scrolling chat tabs — anchoring every goal in a read-only living report, enforced cross-model review, 0-token watches, and governed Markdown memory.
              </p>

              <div className="site-terminal" id="install">
                <div className="site-terminal-top">
                  <span className="site-terminal-label">ONE-LINE INSTALL · NO .NET RUNTIME REQUIRED</span>
                  <button type="button" className="site-copy-btn" onClick={handleCopy}>
                    {copied ? 'Copied' : 'Copy'}
                  </button>
                </div>
                <div className="site-terminal-body">
                  <span className="site-prompt" aria-hidden="true">$</span>
                  <code>{INSTALL_COMMAND}</code>
                </div>
                <div className="site-terminal-foot">
                  <span>Then once: <code>perga auth init-owner --username owner --allow-migrate</code></span>
                  <span>Run: <code>perga</code></span>
                </div>
              </div>

              <div className="site-hero-links">
                <a className="site-btn-primary" href="https://github.com/keanji-x/perga/releases" target="_blank" rel="noreferrer">Download Release</a>
                <a className="site-btn-secondary" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">Inspect Source</a>
                <a className="site-btn-text" href="https://github.com/keanji-x/perga/blob/main/docs/product/design.md" target="_blank" rel="noreferrer">Product Design Notes →</a>
              </div>
            </div>

            <aside className="site-colophon" aria-label="Technical colophon">
              <div className="site-colophon-row">
                <span className="site-col-k">PARADIGM</span>
                <span className="site-col-v">Conversation in the margin.<br />Kernel-verified report at center.</span>
              </div>
              <div className="site-colophon-row">
                <span className="site-col-k">CLIs</span>
                <span className="site-col-v">Claude Code · Codex · ACP<br /><small>Use your existing subscriptions</small></span>
              </div>
              <div className="site-colophon-row">
                <span className="site-col-k">PROTOCOLS</span>
                <span className="site-col-v">Zero-template start, or attach<br />27–40 line <code>.perga.js</code> specs</span>
              </div>
              <div className="site-colophon-row">
                <span className="site-col-k">ISOLATION</span>
                <span className="site-col-v">Worktree · Jujutsu <code>jj</code><br />Linux OverlayFS · Remote SSH</span>
              </div>
              <div className="site-colophon-row">
                <span className="site-col-k">FOOTPRINT</span>
                <span className="site-col-v">Self-contained binary + Rust PTY<br />Single SQLite WAL · Node.js 24+</span>
              </div>
            </aside>
          </div>
        </section>

        {/* REAL PERGA UI COMPONENTS & PROTOCOL SHOWCASE */}
        <section className="site-specimen" id="specimen">
          <div className="site-section-bar">
            <div className="site-bar-left">
              <span className="site-index-tag">FIG. 01</span>
              <span className="site-bar-title">BUILT-IN PROTOCOLS · RENDERED BY REAL PERGA COMPONENTS</span>
            </div>
            <div className="site-tabs" role="tablist" aria-label="Perga built-in protocols">
              {(['delivery', 'equity', 'okr', 'memory'] as const).map((key) => (
                <button
                  key={key}
                  type="button"
                  role="tab"
                  aria-selected={activeTab === key}
                  className={`site-tab-btn${activeTab === key ? ' is-active' : ''}`}
                  data-tab={key}
                  onClick={() => {
                    setActiveTab(key);
                    setNotice(null);
                  }}
                >
                  {PROTOCOL_META[key].tabLabel}
                </button>
              ))}
            </div>
          </div>

          <div className="site-window">
            <div className="site-window-bar">
              <div className="site-window-left">
                <span className="site-win-dot" /><span className="site-win-dot" /><span className="site-win-dot" />
                <span className="site-win-path">{meta.path}</span>
              </div>
              <span className="site-win-chip">{meta.chip}</span>
            </div>

            {notice && (
              <div className="site-intent-banner" role="status">
                <span>{notice}</span>
                <button type="button" onClick={() => setNotice(null)}>Dismiss</button>
              </div>
            )}

            <div className="site-window-body">
              {/* LEFT / MAIN PANE: ACTUAL PERGA COMPONENTS */}
              <article className="site-sheet">
                <div className="site-sheet-datum">
                  <span>{meta.datumLeft}</span>
                  <span>{meta.datumRight}</span>
                </div>

                {activeTab === 'delivery' && (
                  <div className="site-protocol-view" data-panel="delivery">
                    <h2 className="site-doc-h1">Delivery Flow</h2>
                    <p className="site-doc-prose">
                      A real <code>builtin:flow</code> projection from <code>agents-delivery</code>: parallel implementation behind host test gates, enforced read-only cross-model review, and a 0-token GitHub PR check watch. Click any step below to inspect its gate output and model binding.
                    </p>
                    <ReportFlowBlock
                      flow={DEV_DELIVERY_FLOW}
                      title="Unified Track Flow"
                      onOpenWorker={(sid) => showNotice(`In Perga, this focuses live Worker session ${sid} in the side pane.`)}
                    />
                  </div>
                )}

                {activeTab === 'equity' && (
                  <div className="site-protocol-view" data-panel="equity">
                    <h2 className="site-doc-h1">Valuation Range · NVDA</h2>
                    <p className="site-doc-prose">
                      The real <code>widgets/range-axis.html</code> iframe from <code>equity-research</code>. Hover or click any valuation anchor below to inspect its assumption and link directly to the local valuation workpaper.
                    </p>
                    <WidgetFrame
                      src="./widgets/range-axis.html"
                      title="NVDA valuation range axis"
                      data={EQUITY_VALUATION_AXIS}
                      initialHeight={128}
                      onIntent={handleWidgetIntent}
                    />

                    <h2 className="site-doc-h1 site-doc-h1-spaced">Event Expectation Chronicle</h2>
                    <EditorialChronicle
                      payload={{ items: EQUITY_CHRONICLE_ITEMS }}
                    />

                    <h2 className="site-doc-h1 site-doc-h1-spaced">Scheduled Review Flow</h2>
                    <ReportFlowBlock
                      flow={EQUITY_FLOW}
                      title="Weekly Refresh & Catalyst Checks"
                    />
                  </div>
                )}

                {activeTab === 'okr' && (
                  <div className="site-protocol-view" data-panel="okr">
                    <h2 className="site-doc-h1">Key Results Ledger</h2>
                    <p className="site-doc-prose">
                      The real <code>builtin:table</code> component from <code>okr-loop</code>: kernel-stamped metric updates paired with a daily 0-token collection probe and enforced read-only triage.
                    </p>
                    <EditorialTable payload={OKR_LEDGER_TABLE} />

                    <h2 className="site-doc-h1 site-doc-h1-spaced">Daily Optimization Loop</h2>
                    <ReportFlowBlock
                      flow={OKR_FLOW}
                      title="Cron Metric Collection & Triage"
                      onOpenWorker={(sid) => showNotice(`In Perga, this opens active optimizer session ${sid} for live Steer.`)}
                    />
                  </div>
                )}

                {activeTab === 'memory' && (
                  <div className="site-protocol-view" data-panel="memory">
                    <h2 className="site-doc-h1">Pinned Rules</h2>
                    <p className="site-doc-prose">
                      The real <code>entries.html</code> widget from Perga&apos;s Memory Planner track. Pinned rules obey strict UTF-8 byte budgets (<code>Area 1,228 B</code> + <code>Global 820 B</code>) so shared context never bloats across Claude Code, Codex, and ACP.
                    </p>
                    <WidgetFrame
                      src="./widgets/entries.html"
                      title="Pinned memory entries"
                      data={MEMORY_PINNED_DATA}
                      initialHeight={250}
                      onIntent={handleWidgetIntent}
                    />

                    <h2 className="site-doc-h1 site-doc-h1-spaced">Notes &amp; Topics</h2>
                    <WidgetFrame
                      src="./widgets/entries.html"
                      title="Memory notes and topics"
                      data={MEMORY_TOPICS_DATA}
                      initialHeight={210}
                      onIntent={handleWidgetIntent}
                    />
                  </div>
                )}
              </article>

              {/* RIGHT MARGIN: CONVERSATION & GOVERNANCE CONTEXT */}
              <aside className="site-margin" aria-label="Side-pane conversation">
                <div className="site-margin-cap">
                  <span>{meta.marginCap}</span>
                  <span className="site-live-tag">● {meta.marginTag}</span>
                </div>

                {activeTab === 'delivery' && (
                  <>
                    <div className="site-turn">
                      <div className="site-turn-meta"><strong>Codex</strong> · review (read_only) · 14s</div>
                      <p>Race in <code>TrackActor.SessionInputs</code>: if a cancel arrives while an attachment preview resolves, the queued turn keeps the prior epoch&apos;s preview URL.</p>
                    </div>
                    <div className="site-turn site-turn-human">
                      <div className="site-turn-meta"><strong>You</strong> · Steer (injected mid-turn)</div>
                      <p>Keep the fix inside <code>TrackActor.SessionInputs</code> and add a fresh-host restart test. Cite <code>@area/auth-boundaries</code>.</p>
                    </div>
                    <div className="site-turn site-turn-ok">
                      <div className="site-turn-meta"><strong>Host Gate</strong> · deterministic verification</div>
                      <p><code>dotnet test Perga.sln</code> — 412 passed, 0 failed. Contract workers cannot bypass gates or call <code>flow_commit</code>.</p>
                    </div>
                  </>
                )}

                {activeTab === 'equity' && (
                  <>
                    <div className="site-turn">
                      <div className="site-turn-meta"><strong>Lead (Claude)</strong> · post-earnings update</div>
                      <p>Logged Q3 actuals against pre-committed expectations in <code>eventChronicle</code> and refreshed spot price to $138.40 via <code>data_patch</code>.</p>
                    </div>
                    <div className="site-turn site-turn-human">
                      <div className="site-turn-meta"><strong>Codex</strong> · event_review (enforced read_only)</div>
                      <p>Counter-check: gross margin landed 10 bps under midpoint. Keep base target at $155, do not raise multiple until CoWoS-L rack cadence clears 18k units.</p>
                    </div>
                    <div className="site-turn site-turn-ok">
                      <div className="site-turn-meta"><strong>Protocol Rule</strong> · 38 lines</div>
                      <p>Same Perga kernel — zero finance hardcoding. Just <code>schema.js</code>, <code>views.js</code>, and a 124 px seamless HTML axis widget.</p>
                    </div>
                  </>
                )}

                {activeTab === 'okr' && (
                  <>
                    <div className="site-turn">
                      <div className="site-turn-meta"><strong>Host Cron</strong> · 08:00 UTC · 0 tokens</div>
                      <p>Ran <code>scripts/collect-kr-metrics.sh</code>. KR1, KR2, and KR4 met targets; KR3 flake rate rose to 1.8% and woke the Lead.</p>
                    </div>
                    <div className="site-turn site-turn-human">
                      <div className="site-turn-meta"><strong>You</strong> · Steer on optimizer worker</div>
                      <p>Don&apos;t increase Playwright timeout — fix the Escape focus restoration race and require 10 consecutive clean runs.</p>
                    </div>
                    <div className="site-turn site-turn-ok">
                      <div className="site-turn-meta"><strong>Kernel Timestamp</strong> · non-forgeable</div>
                      <p>Table cell timestamps and gate verdicts are stamped by the kernel on commit, never hand-typed by an agent.</p>
                    </div>
                  </>
                )}

                {activeTab === 'memory' && (
                  <>
                    <div className="site-turn">
                      <div className="site-turn-meta"><strong>Task Lead (Claude)</strong> · memory_propose</div>
                      <p>Task sessions are read-only on durable memory. Proposed <code>area/queued-turn-isolation</code> after PR #180 merged.</p>
                    </div>
                    <div className="site-turn site-turn-human">
                      <div className="site-turn-meta"><strong>Memory Planner</strong> · budget &amp; ETag check</div>
                      <p>Checked for conflict with pinned rules; filed under Topic Index to preserve pinned byte headroom.</p>
                    </div>
                    <div className="site-turn site-turn-ok">
                      <div className="site-turn-meta"><strong>Cross-CLI Sync</strong> · hash deduplicated</div>
                      <p>Claude Code, Codex, and ACP sessions in this Area automatically receive the updated delta — or cite any note via <code>@area/key</code>.</p>
                    </div>
                  </>
                )}

                <div className="site-margin-foot">
                  <div className="site-protocol-source">
                    <span>Source</span>
                    <a href={`https://github.com/keanji-x/perga/blob/main/${meta.protocolFile}`} target="_blank" rel="noreferrer">
                      <code>{meta.protocolFile}</code>
                    </a>
                  </div>
                  <div className="site-composer-mock">
                    <span>Steer worker, switch model, or @mention memory…</span>
                    <kbd>↵</kbd>
                  </div>
                </div>
              </aside>
            </div>
          </div>
        </section>

        {/* EDITORIAL CONTRAST LEDGER */}
        <section className="site-thesis" id="thesis">
          <div className="site-section-bar">
            <div className="site-bar-left">
              <span className="site-index-tag">SEC. 02</span>
              <span className="site-bar-title">WHY NOT FIVE CLI TABS?</span>
            </div>
            <span className="site-bar-note">Designed for day three of a real task, not minute one.</span>
          </div>

          <div className="site-thesis-list">
            <article className="site-thesis-row">
              <div className="site-thesis-index">01</div>
              <div className="site-thesis-head">
                <h3>Living reports over drifting chat</h3>
                <p className="site-thesis-old"><s>Scroll through 400 lines of terminal output to remember what changed.</s></p>
              </div>
              <div className="site-thesis-body">
                <p>Conversation stays in the side pane. The main view is a read-only editorial report combining schema-validated ledgers with kernel-stamped facts (actual model, gate exit codes, timestamps) that agents cannot forge. Context limits trigger native compaction or report-anchored epoch handover.</p>
                <span className="site-proof-chip">ReportDoc · Optimistic block locks · Epoch handover</span>
              </div>
            </article>

            <article className="site-thesis-row">
              <div className="site-thesis-index">02</div>
              <div className="site-thesis-head">
                <h3>Governed local memory over auto-memory bloat</h3>
                <p className="site-thesis-old"><s>Choose between goldfish amnesia and a 600-line polluted CLAUDE.md.</s></p>
              </div>
              <div className="site-thesis-body">
                <p>Durable knowledge lives as readable UTF-8 Markdown scoped by Area and Global, shared across Claude Code, Codex, and ACP. Task leads can only propose updates; a dedicated Memory Planner enforces ETags, hard byte budgets, and hash-deduplicated injection — plus <code>@mention</code> recall and one-click Save.</p>
                <span className="site-proof-chip">Area 1228 B · Global 820 B · Index 1024 B · ETag</span>
              </div>
            </article>

            <article className="site-thesis-row">
              <div className="site-thesis-index">03</div>
              <div className="site-thesis-head">
                <h3>Enforced cross-model review &amp; live Steer</h3>
                <p className="site-thesis-old"><s>Let the same model write the code, weaken the test, and declare victory.</s></p>
              </div>
              <div className="site-thesis-body">
                <p>Orchestrate Claude Code, Codex, and ACP inside the same track. Execution is structurally separated from acceptance, reviews can be enforced read-only on a second provider, write tasks pass through host verification gates, and you can steer or swap models on the live worker stream anytime.</p>
                <span className="site-proof-chip">routing.enforce · Host Gate · Shared human/Lead Steer</span>
              </div>
            </article>

            <article className="site-thesis-row">
              <div className="site-thesis-index">04</div>
              <div className="site-thesis-head">
                <h3>0-token watches &amp; real local isolation</h3>
                <p className="site-thesis-old"><s>Burn tokens polling CI or wait ten minutes per Git worktree rebuild.</s></p>
              </div>
              <div className="site-thesis-body">
                <p>Declarative <code>flow.json</code> runs zero-token probes (<code>prChecks</code> or shell commands) and wakes the Lead only when state changes. Isolate tracks via Git worktrees, native Jujutsu (<code>jj</code>), Linux OverlayFS prewarmed build snapshots, or remote SSH targets — with a persistent Rust PTY bridge.</p>
                <span className="site-proof-chip">0-token watch · Linux OverlayFS · Native jj · Remote SSH</span>
              </div>
            </article>
          </div>
        </section>

        {/* SYSTEM SPECIFICATIONS */}
        <section className="site-specs" id="specs">
          <div className="site-section-bar">
            <div className="site-bar-left">
              <span className="site-index-tag">SEC. 03</span>
              <span className="site-bar-title">SYSTEM SPECIFICATIONS</span>
            </div>
            <span className="site-bar-note">100% local sovereignty. Zero cloud lock-in.</span>
          </div>

          <div className="site-spec-table">
            <div className="site-spec-row">
              <div className="site-spec-k">Host Binary</div>
              <div className="site-spec-v">Self-contained .NET 11 native packages for <strong>macOS Apple Silicon (arm64)</strong> and <strong>Linux x64</strong> (WSL2 supported). No .NET runtime required.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Frontend</div>
              <div className="site-spec-v">Lightweight React + TypeScript web app &amp; installable PWA — no Electron overhead across parallel sessions.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Terminal Bridge</div>
              <div className="site-spec-v">Independent Rust daemon (<code>perga-term-bridge</code>, <code>#![forbid(unsafe_code)]</code>) — persistent PTY sessions survive host restarts and hot upgrades.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Agent Providers</div>
              <div className="site-spec-v">Claude Code, Codex, and ACP-compatible CLIs via bundled OAR bridge (requires Node.js 24+). Use your existing provider subscriptions.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Workspaces</div>
              <div className="site-spec-v">Git worktrees, Jujutsu (<code>jj</code>), Linux OverlayFS with shared prewarmed build caches, and remote SSH execution with reverse MCP tunnel.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Storage &amp; Memory</div>
              <div className="site-spec-v">Single local SQLite WAL database + readable UTF-8 Markdown memory files with frontmatter and ETag concurrency control.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Protocols</div>
              <div className="site-spec-v">Zero-template start by default. Attach 27–40 line <code>.perga.js</code> protocols (<code>agents-delivery</code>, <code>okr-loop</code>, <code>equity-research</code>, <code>memory</code>) only when rules recur.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">License</div>
              <div className="site-spec-v">Apache License 2.0.</div>
            </div>
          </div>
        </section>

        {/* DARK MONOLITH CLOSING */}
        <section className="site-closing">
          <div className="site-closing-inner">
            <div className="site-closing-copy">
              <p className="site-closing-kicker">BUILT IN THE OPEN · DOGFOODED DAILY</p>
              <h2 className="site-closing-title">Built with Perga <em>itself.</em></h2>
              <p className="site-closing-desc">
                Every release of Perga is developed, cross-reviewed, and verified inside Perga tracks. If you already run multiple Claude Code or Codex sessions on macOS or Linux, try the self-contained release in two minutes.
              </p>
              <div className="site-closing-cmd">
                <code>{INSTALL_COMMAND}</code>
              </div>
            </div>
            <div className="site-closing-actions">
              <a className="site-closing-btn-light" href="https://github.com/keanji-x/perga/releases" target="_blank" rel="noreferrer">Download Release</a>
              <a className="site-closing-btn-outline" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub Repository</a>
              <a className="site-closing-btn-outline" href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.md" target="_blank" rel="noreferrer">Read Vision</a>
              {/* DISCORD SLOT: Uncomment when ready
              <a className="site-closing-btn-light" href="https://discord.gg/YOUR_INVITE" target="_blank" rel="noreferrer">Join Early Discord</a>
              */}
            </div>
          </div>
        </section>
      </main>

      <footer className="site-footer">
        <div className="site-footer-inner">
          <span>PERGA · APACHE 2.0 · LOCAL-FIRST MULTI-AGENT WORKSPACE</span>
          <div className="site-footer-links">
            <a href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub</a>
            <a href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.md" target="_blank" rel="noreferrer">Vision</a>
            <a href="https://github.com/keanji-x/perga/blob/main/docs/product/design.md" target="_blank" rel="noreferrer">Design</a>
            <a href="https://github.com/keanji-x/perga/blob/main/DESIGN.md" target="_blank" rel="noreferrer">Architecture</a>
            <a href="./install.sh">install.sh</a>
          </div>
        </div>
      </footer>
    </div>
  );
}
