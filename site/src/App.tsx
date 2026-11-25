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

type ProtocolShowcase = 'equity' | 'okr';

const INSTALL_COMMAND = 'curl -fsSL https://keanji-x.github.io/perga/install.sh | sh';

export function App() {
  const [protocolTab, setProtocolTab] = useState<ProtocolShowcase>('equity');
  const [copied, setCopied] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const showNotice = useCallback((text: string) => {
    setNotice(text);
    window.setTimeout(() => {
      setNotice((current) => (current === text ? null : current));
    }, 3400);
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

  return (
    <div className="site-frame">
      <header className="site-masthead">
        <div className="site-masthead-inner">
          <a className="site-wordmark" href="./" aria-label="Perga home">
            <span className="site-seal" aria-hidden="true">P</span>
            <span className="site-wordmark-text">Perga</span>
          </a>
          <nav className="site-nav" aria-label="Primary">
            <a href="#act-1">The Problem</a>
            <a href="#act-2">Flow</a>
            <a href="#act-3">Memory</a>
            <a href="#act-4">Protocols</a>
            <a href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.en.md" target="_blank" rel="noreferrer">Vision</a>
            <a className="site-nav-repo" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub ↗</a>
            {/* DISCORD SLOT: Uncomment when ready
            <a className="site-nav-repo" href="https://discord.gg/YOUR_INVITE" target="_blank" rel="noreferrer">Discord ↗</a>
            */}
          </nav>
        </div>
      </header>

      {notice && (
        <div className="site-toast" role="status">
          <span>{notice}</span>
          <button type="button" onClick={() => setNotice(null)}>Dismiss</button>
        </div>
      )}

      <main>
        {/* OPENING: Calm, spacious, single focus */}
        <section className="site-hero">
          <div className="site-hero-inner">
            <p className="site-kicker">
              <span className="site-kicker-dot" aria-hidden="true" />
              <span>APACHE 2.0 · MACOS APPLE SILICON &amp; LINUX X64 · 100% LOCAL</span>
            </p>
            <h1 className="site-display">
              Chat drifts.<br />
              <em>Reports stay.</em>
            </h1>
            <p className="site-tagline">
              A local workspace for multi-day <strong>Claude Code</strong> &amp; <strong>Codex</strong> work.
            </p>

            <div className="site-terminal" id="install">
              <div className="site-terminal-top">
                <span className="site-terminal-label">SELF-CONTAINED INSTALL · NO .NET RUNTIME REQUIRED</span>
                <button type="button" className="site-copy-btn" onClick={handleCopy}>
                  {copied ? 'Copied' : 'Copy'}
                </button>
              </div>
              <div className="site-terminal-body">
                <span className="site-prompt" aria-hidden="true">$</span>
                <code>{INSTALL_COMMAND}</code>
              </div>
              <div className="site-terminal-foot">
                <span>First run: <code>perga auth init-owner --username owner --allow-migrate</code></span>
                <span>Start: <code>perga</code></span>
              </div>
            </div>

            <div className="site-hero-actions">
              <a className="site-btn-primary" href="https://github.com/keanji-x/perga/releases" target="_blank" rel="noreferrer">Download Release</a>
              <a className="site-btn-secondary" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">View on GitHub</a>
              <a className="site-btn-link" href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.en.md" target="_blank" rel="noreferrer">Read Vision →</a>
            </div>
          </div>
        </section>

        {/* ACT I: THE TENSION (Day 1 vs Day 3) */}
        <section className="site-chapter" id="act-1">
          <div className="site-chapter-rail">
            <span className="site-chapter-num">I.</span>
            <span className="site-chapter-label">DAY ONE VS. DAY THREE</span>
          </div>

          <div className="site-story-split">
            <div className="site-story-col">
              <p className="site-mini-kicker">DAY ONE</p>
              <h2 className="site-chapter-h2">A single chat tab feels effortless.</h2>
              <p className="site-prose">
                You drop in a bug, watch tokens stream across the terminal, run tests once, and merge. For thirty-minute tasks, a scrolling transcript is all you need.
              </p>
            </div>
            <div className="site-story-col site-story-col-contrast">
              <p className="site-mini-kicker">DAY THREE</p>
              <h2 className="site-chapter-h2">The same chat tab quietly breaks down.</h2>
              <ul className="site-tension-list">
                <li><strong>State dissolves in chat.</strong> Architectural decisions and half-finished steps vanish 400 lines up.</li>
                <li><strong>One model grades its own homework.</strong> The same session writes the patch, softens a test, and declares victory.</li>
                <li><strong>Memory either vanishes or rots.</strong> Fresh terminals forget yesterday; unchecked <code>CLAUDE.md</code> or auto-memory files bloat and contradict themselves.</li>
                <li><strong>Waiting wastes both time and tokens.</strong> Fragile bash loops poll CI, and parallel Git worktrees cold-rebuild your repository from scratch.</li>
              </ul>
            </div>
          </div>

          <blockquote className="site-bridge-quote">
            The bottleneck is no longer model intelligence — it&apos;s that a scrolling chat stream is the wrong container for multi-day work.
          </blockquote>
        </section>

        {/* ACT II: THE FLIP — LIVING REPORT & ENFORCED CROSS-MODEL FLOW */}
        <section className="site-chapter" id="act-2">
          <div className="site-chapter-rail">
            <span className="site-chapter-num">II.</span>
            <span className="site-chapter-label">MOVE STATE OUT OF CHAT</span>
          </div>

          <div className="site-chapter-intro">
            <h2 className="site-chapter-h2">
              Each goal becomes an isolated <em>Track</em> — anchored in a living report and a kernel-verified flow.
            </h2>
            <p className="site-prose">
              Conversation retreats to the margin. The main pane stays a quiet, read-only sheet where progress, actual model bindings, and host test gates are stamped by the kernel — never hand-typed by an agent. Below is Perga&apos;s actual <code>builtin:flow</code> component from <code>agents-delivery</code>. <strong>Click any step</strong> to inspect its gate output or watch status.
            </p>
          </div>

          <div className="site-stage">
            <div className="site-stage-bar">
              <span>REAL COMPONENT · <code>ReportFlowBlock</code></span>
              <span>PROTOCOL · <code>agents-delivery</code> (28 LINES)</span>
            </div>
            <div className="site-stage-sheet">
              <ReportFlowBlock
                flow={DEV_DELIVERY_FLOW}
                title="Unified Track Flow · PR #180"
                onOpenWorker={(sid) => showNotice(`In Perga, this opens live Worker session ${sid} so you can observe or steer mid-turn.`)}
              />
            </div>
          </div>

          <div className="site-three-notes">
            <div className="site-note">
              <h3>Enforced cross-model review</h3>
              <p>Let Claude implement behind a host test gate, and force Codex to audit the diff in strict <code>read_only</code> mode.</p>
            </div>
            <div className="site-note">
              <h3>0-token external watches</h3>
              <p>Declarative <code>prChecks</code> and shell probes wait on CI from the host at zero token cost, waking the Lead only on state change.</p>
            </div>
            <div className="site-note">
              <h3>Shared human &amp; Lead Steer</h3>
              <p>Workers are never black boxes — you observe the exact same live stream and persistent Rust PTY, and can steer or swap models mid-run.</p>
            </div>
          </div>
        </section>

        {/* ACT III: MEMORY THAT DOESN'T ROT */}
        <section className="site-chapter site-chapter-paper" id="act-3">
          <div className="site-chapter-rail">
            <span className="site-chapter-num">III.</span>
            <span className="site-chapter-label">MEMORY ACROSS TRACKS &amp; CLIs</span>
          </div>

          <div className="site-chapter-intro">
            <h2 className="site-chapter-h2">
              One readable local brain — shared across Claude Code, Codex, and ACP without context bloat.
            </h2>
            <p className="site-prose">
              A living report keeps one track coherent across days. When durable lessons need to carry into next week&apos;s track — or across a different vendor&apos;s CLI — Perga stores them as plain UTF-8 Markdown scoped by <strong>Area</strong> and <strong>Global</strong>. Task leads can only call <code>memory_propose</code>; a dedicated <strong>Memory Planner</strong> governs writes with ETag locking and strict byte budgets (<code>1,228 B</code> Area + <code>820 B</code> Global). Below is Perga&apos;s actual <code>entries.html</code> widget — click any topic to expand.
            </p>
          </div>

          <div className="site-stage">
            <div className="site-stage-bar">
              <span>REAL WIDGET · <code>Memory/widgets/entries.html</code></span>
              <span>742 B / 2,048 B PINNED · ETAG LOCKED</span>
            </div>
            <div className="site-stage-sheet">
              <div className="site-subhead">Pinned Rules</div>
              <WidgetFrame
                src="./widgets/entries.html"
                title="Perga pinned memory entries"
                data={MEMORY_PINNED_DATA}
                initialHeight={240}
                onIntent={handleWidgetIntent}
              />
              <div className="site-subhead site-subhead-spaced">Notes &amp; Topics</div>
              <WidgetFrame
                src="./widgets/entries.html"
                title="Perga topic memory entries"
                data={MEMORY_TOPICS_DATA}
                initialHeight={210}
                onIntent={handleWidgetIntent}
              />
            </div>
          </div>
        </section>

        {/* ACT IV: ONE KERNEL, MANY LONG-HORIZON PROTOCOLS */}
        <section className="site-chapter" id="act-4">
          <div className="site-chapter-rail">
            <span className="site-chapter-num">IV.</span>
            <span className="site-chapter-label">ONE KERNEL · 30-LINE PROTOCOLS</span>
          </div>

          <div className="site-chapter-intro site-chapter-intro-row">
            <div>
              <h2 className="site-chapter-h2">
                Start with zero templates. Add structure only when rules recur.
              </h2>
              <p className="site-prose">
                Because the kernel only owns state, scheduling, routing, and gates, the exact same engine adapts to any long-horizon discipline with a 27–40 line <code>.perga.js</code> protocol. Switch between two built-in protocols rendered live below:
              </p>
            </div>
            <div className="site-protocol-switch" role="tablist" aria-label="Built-in protocols">
              <button
                type="button"
                role="tab"
                aria-selected={protocolTab === 'equity'}
                className={`site-tab-btn${protocolTab === 'equity' ? ' is-active' : ''}`}
                data-tab="equity"
                onClick={() => setProtocolTab('equity')}
              >
                equity-research
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={protocolTab === 'okr'}
                className={`site-tab-btn${protocolTab === 'okr' ? ' is-active' : ''}`}
                data-tab="okr"
                onClick={() => setProtocolTab('okr')}
              >
                okr-loop
              </button>
            </div>
          </div>

          <div className="site-stage">
            {protocolTab === 'equity' ? (
              <>
                <div className="site-stage-bar">
                  <span>REAL WIDGETS · <code>range-axis.html</code> + <code>EditorialChronicle</code> + <code>ReportFlowBlock</code></span>
                  <a href="https://github.com/keanji-x/perga/tree/main/templates/equity-research" target="_blank" rel="noreferrer">
                    <code>templates/equity-research</code> ↗
                  </a>
                </div>
                <div className="site-stage-sheet" data-panel="equity">
                  <div className="site-subhead">Valuation Range · NVDA (hover or click anchors)</div>
                  <WidgetFrame
                    src="./widgets/range-axis.html"
                    title="NVDA valuation range axis"
                    data={EQUITY_VALUATION_AXIS}
                    initialHeight={128}
                    onIntent={handleWidgetIntent}
                  />
                  <div className="site-subhead site-subhead-spaced">Event Expectation Chronicle</div>
                  <EditorialChronicle payload={{ items: EQUITY_CHRONICLE_ITEMS }} />
                  <div className="site-subhead site-subhead-spaced">Scheduled Review Flow</div>
                  <ReportFlowBlock flow={EQUITY_FLOW} title="Weekly Valuation & Catalyst Checks" />
                </div>
              </>
            ) : (
              <>
                <div className="site-stage-bar">
                  <span>REAL COMPONENTS · <code>EditorialTable</code> + <code>ReportFlowBlock</code></span>
                  <a href="https://github.com/keanji-x/perga/tree/main/templates/okr-loop" target="_blank" rel="noreferrer">
                    <code>templates/okr-loop</code> ↗
                  </a>
                </div>
                <div className="site-stage-sheet" data-panel="okr">
                  <div className="site-subhead">Key Results Ledger</div>
                  <EditorialTable payload={OKR_LEDGER_TABLE} />
                  <div className="site-subhead site-subhead-spaced">Daily 0-Token Collection &amp; Triage Flow</div>
                  <ReportFlowBlock
                    flow={OKR_FLOW}
                    title="Cron Metric Probe & Optimization"
                    onOpenWorker={(sid) => showNotice(`In Perga, this opens active optimizer session ${sid} for live Steer.`)}
                  />
                </div>
              </>
            )}
          </div>
        </section>

        {/* ACT V: LOCAL SOVEREIGNTY & SPECS */}
        <section className="site-chapter site-chapter-paper" id="specs">
          <div className="site-chapter-rail">
            <span className="site-chapter-num">V.</span>
            <span className="site-chapter-label">LOCAL SOVEREIGNTY</span>
          </div>

          <div className="site-spec-table">
            <div className="site-spec-row">
              <div className="site-spec-k">Host Binary</div>
              <div className="site-spec-v">Self-contained .NET 11 native packages for <strong>macOS Apple Silicon (arm64)</strong> and <strong>Linux x64</strong> (WSL2 supported). No .NET runtime required.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Terminal Bridge</div>
              <div className="site-spec-v">Native Rust PTY daemon (<code>perga-term-bridge</code>, <code>#![forbid(unsafe_code)]</code>) — survives host restarts and hot upgrades.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Workspaces</div>
              <div className="site-spec-v">Git worktrees, native Jujutsu (<code>jj</code>), Linux OverlayFS prewarmed build snapshots, and remote SSH execution with reverse MCP tunnel.</div>
            </div>
            <div className="site-spec-row">
              <div className="site-spec-k">Providers &amp; State</div>
              <div className="site-spec-v">Claude Code, Codex, and ACP CLIs (Node.js 24+ OAR bridge). Local SQLite WAL + plain UTF-8 Markdown memory. Zero cloud lock-in.</div>
            </div>
          </div>
        </section>

        {/* CLOSING MONOLITH */}
        <section className="site-closing">
          <div className="site-closing-inner">
            <div className="site-closing-copy">
              <p className="site-closing-kicker">DOGFOODED DAILY · APACHE LICENSE 2.0</p>
              <h2 className="site-closing-title">Built with Perga <em>itself.</em></h2>
              <p className="site-closing-desc">
                Every release of Perga is planned, cross-reviewed, and verified inside Perga tracks. Try the self-contained release on your own machine in two minutes.
              </p>
              <div className="site-closing-cmd">
                <code>{INSTALL_COMMAND}</code>
              </div>
            </div>
            <div className="site-closing-actions">
              <a className="site-closing-btn-light" href="https://github.com/keanji-x/perga/releases" target="_blank" rel="noreferrer">Download Release</a>
              <a className="site-closing-btn-outline" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub</a>
              <a className="site-closing-btn-outline" href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.en.md" target="_blank" rel="noreferrer">Vision (EN)</a>
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
            <a href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.en.md" target="_blank" rel="noreferrer">Vision</a>
            <a href="https://github.com/keanji-x/perga/blob/main/DESIGN.md" target="_blank" rel="noreferrer">Architecture</a>
            <a href="./install.sh">install.sh</a>
          </div>
        </div>
      </footer>
    </div>
  );
}
