import { useCallback, useState } from 'react';
import { ReportFlowBlock } from '../../fe/web/src/features/report/flow/public.tsx';
import { EditorialChronicle } from '../../fe/web/src/features/report/editorial/chronicle.tsx';
import pergaMarkUrl from '../../fe/web/src/ui/brand/perga-mark.svg';
import { WidgetFrame } from './WidgetFrame.tsx';
import {
  DEV_DELIVERY_FLOW,
  DEV_DELIVERY_FLOW_JSON_EXCERPT,
  EQUITY_CHRONICLE_ITEMS,
  EQUITY_VALUATION_AXIS,
  MEMORY_PINNED_DATA,
  MEMORY_TOPICS_DATA,
} from './showcaseData.ts';

type TrackTab = 'delivery' | 'invest';

const INSTALL_COMMAND = 'curl -fsSL https://keanji-x.github.io/perga/install.sh | sh';

const TRACK_TABS: ReadonlyArray<Readonly<{
  id: TrackTab;
  label: string;
  capability: string;
  trackName: string;
  meta: string;
  sourceUrl: string;
  sourceLabel: string;
}>> = [
  {
    id: 'delivery',
    label: 'Engineering',
    capability: 'MULTI-AGENT FLOW',
    trackName: 'auth-session-hardening',
    meta: 'Claude implements · Codex reviews read-only · Waits for CI',
    sourceUrl: 'https://github.com/keanji-x/perga/tree/main/templates/agents-delivery',
    sourceLabel: 'agents-delivery',
  },
  {
    id: 'invest',
    label: 'Invest',
    capability: 'LIVING REPORT',
    trackName: 'NVDA · long-horizon thesis',
    meta: 'Valuation axis · Pre-committed expectations · Weekly updates',
    sourceUrl: 'https://github.com/keanji-x/perga/tree/main/templates/equity-research',
    sourceLabel: 'equity-research',
  },
];

export function App() {
  const [trackTab, setTrackTab] = useState<TrackTab>('delivery');
  const [showFlowJson, setShowFlowJson] = useState(false);
  const [copied, setCopied] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const showNotice = useCallback((text: string) => {
    setNotice(text);
    window.setTimeout(() => {
      setNotice((current) => (current === text ? null : current));
    }, 3000);
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
      showNotice(`Opens local file: ${intent.path}`);
    } else if (intent.kind === 'open_track' && typeof intent.trackId === 'string') {
      showNotice(`Opens source Track: ${intent.trackId}`);
    }
  }, [showNotice]);

  const currentTrack = TRACK_TABS.find((t) => t.id === trackTab) ?? TRACK_TABS[0];

  return (
    <div className="site-frame">
      <header className="site-masthead">
        <div className="site-masthead-inner">
          <a className="site-wordmark" href="./" aria-label="Perga home">
            <img className="site-mark" src={pergaMarkUrl} alt="" width="24" height="24" />
            <span className="site-wordmark-text">Perga</span>
          </a>
          <nav className="site-nav" aria-label="Primary">
            <a href="#reports">Reports</a>
            <a href="#memory">Memory</a>
            <a href="./vision.html">Vision</a>
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
          <button type="button" onClick={() => setNotice(null)}>×</button>
        </div>
      )}

      <main>
        {/* 1. REPORT-CENTRIC HERO + TWO LIVE REPORT EXAMPLES */}
        <section className="site-hero">
          <div className="site-hero-top">
            <h1 className="site-display">
              Chat drifts. <em>Reports stay.</em>
            </h1>
            <p className="site-tagline">
              A report-centric local workspace for multi-day <strong>Claude Code</strong> &amp; <strong>Codex</strong> work.
            </p>

            <div className="site-install-row" id="install">
              <div className="site-terminal">
                <span className="site-prompt" aria-hidden="true">$</span>
                <code>{INSTALL_COMMAND}</code>
                <button type="button" className="site-copy-btn" onClick={handleCopy}>
                  {copied ? 'Copied' : 'Copy'}
                </button>
              </div>
              <a className="site-btn-primary" href="https://github.com/keanji-x/perga/releases" target="_blank" rel="noreferrer">Releases</a>
            </div>
            <p className="site-install-note">
              macOS Apple Silicon &amp; Linux x64 · Self-contained binary (no .NET runtime) · Requires Node.js 24+
            </p>
          </div>

          <div className="site-showcase" id="reports">
            <div className="site-showcase-head">
              <div className="site-tabs" role="tablist" aria-label="Report examples">
                {TRACK_TABS.map((tab) => (
                  <button
                    key={tab.id}
                    type="button"
                    role="tab"
                    aria-selected={trackTab === tab.id}
                    className={`site-tab${trackTab === tab.id ? ' is-active' : ''}`}
                    data-tab={tab.id}
                    onClick={() => setTrackTab(tab.id)}
                  >
                    {tab.label}
                  </button>
                ))}
              </div>
              <div className="site-showcase-meta">
                <span>{currentTrack.meta}</span>
                <a href={currentTrack.sourceUrl} target="_blank" rel="noreferrer">
                  {currentTrack.sourceLabel} ↗
                </a>
              </div>
            </div>

            <div className="site-sheet">
              <div className="site-sheet-bar">
                <span className="site-sheet-title">{currentTrack.trackName}</span>
                <div className="site-sheet-right">
                  {trackTab === 'delivery' && (
                    <button
                      type="button"
                      className={`site-code-toggle${showFlowJson ? ' is-open' : ''}`}
                      onClick={() => setShowFlowJson((v) => !v)}
                    >
                      {showFlowJson ? 'Hide flow.json' : 'View flow.json'}
                    </button>
                  )}
                  <span className="site-sheet-badge">{currentTrack.capability}</span>
                </div>
              </div>

              {trackTab === 'delivery' && (
                <div data-panel="delivery">
                  {showFlowJson && (
                    <div className="site-flow-drawer">
                      <div className="site-flow-drawer-head">
                        <span>flow.json (excerpt)</span>
                        <span>Condensed from 5-node graph · Claude impl → Codex review → CI wait</span>
                      </div>
                      <pre className="site-flow-code"><code>{DEV_DELIVERY_FLOW_JSON_EXCERPT}</code></pre>
                    </div>
                  )}
                  <ReportFlowBlock
                    flow={DEV_DELIVERY_FLOW}
                    title="Delivery Flow"
                    onOpenWorker={(sid) => showNotice(`Focuses live Worker ${sid} for mid-turn steering.`)}
                  />
                </div>
              )}

              {trackTab === 'invest' && (
                <div data-panel="invest">
                  <div className="site-subhead">NVDA · Valuation Range</div>
                  <WidgetFrame
                    src="./widgets/range-axis.html"
                    title="NVDA valuation range axis"
                    data={EQUITY_VALUATION_AXIS}
                    initialHeight={124}
                    onIntent={handleWidgetIntent}
                  />
                  <div className="site-subhead site-subhead-spaced">Expectation Chronicle</div>
                  <EditorialChronicle payload={{ items: EQUITY_CHRONICLE_ITEMS }} />
                </div>
              )}
            </div>
          </div>
        </section>

        {/* 2. THREE CRISP DIFFERENCES */}
        <section className="site-pillars">
          <div className="site-pillar">
            <span className="site-pillar-num">01</span>
            <h2>One living report per task</h2>
            <p>Chat stays in the margin. Plans, test results, and decisions stay in one readable document that survives context resets and days away.</p>
          </div>
          <div className="site-pillar">
            <span className="site-pillar-num">02</span>
            <h2>Models check each other</h2>
            <p>Let Claude Code implement behind a local test gate, then hand the diff to Codex in read-only mode. Step in and steer any live worker mid-turn.</p>
          </div>
          <div className="site-pillar">
            <span className="site-pillar-num">03</span>
            <h2>Background flows that wait</h2>
            <p>Declare dependencies, schedules, and CI checks in <code>flow.json</code>. Perga waits locally in the background and wakes a session only when state changes.</p>
          </div>
        </section>

        {/* 3. ONE MORE THING: SHARED MEMORY ACROSS CLIs */}
        <section className="site-memory" id="memory">
          <div className="site-memory-head">
            <span className="site-kicker">ONE MORE THING</span>
            <h2>One memory, shared across CLIs.</h2>
            <p>
              Project rules shouldn&apos;t stay trapped in separate <code>CLAUDE.md</code> files or forgotten chats. Perga keeps them as plain Markdown notes shared across <strong>Claude Code</strong>, <strong>Codex</strong>, and every new task — so you never repeat the same constraints twice.
            </p>
          </div>

          <div className="site-sheet site-sheet-memory">
            <div className="site-sheet-bar">
              <span className="site-sheet-title">Project Memory</span>
              <span className="site-sheet-badge">PLAIN MARKDOWN · SHARED BY CLAUDE CODE &amp; CODEX</span>
            </div>
            <WidgetFrame
              src="./widgets/entries.html"
              title="Perga pinned project rule"
              data={MEMORY_PINNED_DATA}
              initialHeight={180}
              onIntent={handleWidgetIntent}
            />
            <div className="site-memory-divider" />
            <WidgetFrame
              src="./widgets/entries.html"
              title="Perga expandable project notes"
              data={MEMORY_TOPICS_DATA}
              initialHeight={148}
              onIntent={handleWidgetIntent}
            />
          </div>
        </section>

        {/* 4. QUIET LOCAL SPECS + CLOSING */}
        <section className="site-facts">
          <div>
            <span>WORKSPACES</span>
            <strong>Git worktree · Jujutsu <code>jj</code> · Linux OverlayFS · SSH</strong>
          </div>
          <div>
            <span>TERMINALS</span>
            <strong>Persistent Rust PTY</strong>
          </div>
          <div>
            <span>STORAGE</span>
            <strong>Local SQLite + plain Markdown</strong>
          </div>
          <div>
            <span>EXTENSIBLE</span>
            <strong>Start blank or drop in a <code>.perga.js</code> template</strong>
          </div>
        </section>

        <section className="site-closing">
          <div className="site-closing-inner">
            <div>
              <h2>Built with Perga <em>itself.</em></h2>
              <p>Open source under Apache 2.0. Keep your subscriptions, your hardware, and your state.</p>
            </div>
            <div className="site-closing-actions">
              <a className="site-closing-btn-light" href="https://github.com/keanji-x/perga/releases" target="_blank" rel="noreferrer">Download Release</a>
              <a className="site-closing-btn-outline" href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub</a>
              <a className="site-closing-btn-outline" href="./vision.html">Vision</a>
            </div>
          </div>
        </section>
      </main>

      <footer className="site-footer">
        <div className="site-footer-inner">
          <span>PERGA · APACHE 2.0</span>
          <div className="site-footer-links">
            <a href="https://github.com/keanji-x/perga" target="_blank" rel="noreferrer">GitHub</a>
            <a href="./vision.html">Vision</a>
            <a href="https://github.com/keanji-x/perga/blob/main/DESIGN.md" target="_blank" rel="noreferrer">Architecture</a>
            <a href="./install.sh">install.sh</a>
          </div>
        </div>
      </footer>
    </div>
  );
}
