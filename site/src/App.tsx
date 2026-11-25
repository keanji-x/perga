import { useCallback, useState } from 'react';
import { ReportFlowBlock } from '../../fe/web/src/features/report/flow/public.tsx';
import { EditorialTable } from '../../fe/web/src/features/report/editorial/table.tsx';
import { EditorialChronicle } from '../../fe/web/src/features/report/editorial/chronicle.tsx';
import { WidgetFrame } from './WidgetFrame.tsx';
import {
  DEV_DELIVERY_FLOW,
  EQUITY_CHRONICLE_ITEMS,
  EQUITY_VALUATION_AXIS,
  MEMORY_PINNED_DATA,
  MEMORY_TOPICS_DATA,
  OKR_LEDGER_TABLE,
} from './showcaseData.ts';

type TrackTab = 'delivery' | 'research' | 'okr';

const INSTALL_COMMAND = 'curl -fsSL https://keanji-x.github.io/perga/install.sh | sh';

const TRACK_TABS: ReadonlyArray<Readonly<{
  id: TrackTab;
  label: string;
  trackName: string;
  meta: string;
  sourceUrl: string;
  sourceLabel: string;
}>> = [
  {
    id: 'delivery',
    label: 'Delivery',
    trackName: 'auth-session-hardening',
    meta: 'Claude impl · Codex read-only review · 0-token CI watch',
    sourceUrl: 'https://github.com/keanji-x/perga/tree/main/templates/agents-delivery',
    sourceLabel: 'agents-delivery',
  },
  {
    id: 'research',
    label: 'Research',
    trackName: 'NVDA · long-horizon thesis',
    meta: 'Valuation range · Pre-committed expectations · Weekly cron',
    sourceUrl: 'https://github.com/keanji-x/perga/tree/main/templates/equity-research',
    sourceLabel: 'equity-research',
  },
  {
    id: 'okr',
    label: 'OKR',
    trackName: 'q4-latency-and-reliability',
    meta: 'Kernel-stamped KR ledger · Daily 0-token probe',
    sourceUrl: 'https://github.com/keanji-x/perga/tree/main/templates/okr-loop',
    sourceLabel: 'okr-loop',
  },
];

export function App() {
  const [trackTab, setTrackTab] = useState<TrackTab>('delivery');
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
            <span className="site-seal" aria-hidden="true">P</span>
            <span className="site-wordmark-text">Perga</span>
          </a>
          <nav className="site-nav" aria-label="Primary">
            <a href="#tracks">Tracks</a>
            <a href="#memory">Memory</a>
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
          <button type="button" onClick={() => setNotice(null)}>×</button>
        </div>
      )}

      <main>
        {/* 1. HERO + IMMEDIATE PRODUCT */}
        <section className="site-hero">
          <div className="site-hero-top">
            <h1 className="site-display">
              Chat drifts. <em>Reports stay.</em>
            </h1>
            <p className="site-tagline">
              A local workspace for multi-day <strong>Claude Code</strong> &amp; <strong>Codex</strong> work.
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

          {/* LIVE TRACK SHOWCASE RIGHT IN THE FIRST FOLD */}
          <div className="site-showcase" id="tracks">
            <div className="site-showcase-head">
              <div className="site-tabs" role="tablist" aria-label="Track examples">
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
                <span className="site-sheet-badge">LIVING REPORT</span>
              </div>

              {trackTab === 'delivery' && (
                <div data-panel="delivery">
                  <ReportFlowBlock
                    flow={DEV_DELIVERY_FLOW}
                    title="Delivery Flow"
                    onOpenWorker={(sid) => showNotice(`Focuses live Worker ${sid} for mid-turn Steer.`)}
                  />
                </div>
              )}

              {trackTab === 'research' && (
                <div data-panel="research">
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

              {trackTab === 'okr' && (
                <div data-panel="okr">
                  <EditorialTable payload={OKR_LEDGER_TABLE} />
                </div>
              )}
            </div>
          </div>
        </section>

        {/* 2. THREE CRISP DIFFERENCES (ZERO LECTURING) */}
        <section className="site-pillars">
          <div className="site-pillar">
            <span className="site-pillar-num">01</span>
            <h2>State lives in the report</h2>
            <p>Chat stays in the margin. Progress, model bindings, and test exit codes are stamped by the kernel — surviving context resets.</p>
          </div>
          <div className="site-pillar">
            <span className="site-pillar-num">02</span>
            <h2>Models check each other</h2>
            <p>Claude implements behind a host test gate; Codex audits in enforced read-only mode. You can steer any live worker mid-turn.</p>
          </div>
          <div className="site-pillar">
            <span className="site-pillar-num">03</span>
            <h2>Zero tokens while idle</h2>
            <p>Declarative watches probe CI or shell checks locally at zero cost, waking the Lead only when state actually changes.</p>
          </div>
        </section>

        {/* 3. SHARED MEMORY */}
        <section className="site-memory" id="memory">
          <div className="site-memory-head">
            <h2>Shared memory, without the bloat.</h2>
            <p>
              Readable Markdown scoped by <strong>Area</strong> and <strong>Global</strong>, shared across Claude Code, Codex, and ACP. Task sessions can only propose; a Memory Planner governs writes under hard byte budgets.
            </p>
          </div>

          <div className="site-sheet site-sheet-memory">
            <div className="site-sheet-bar">
              <span className="site-sheet-title">Area Memory</span>
              <span className="site-sheet-badge">742 / 1,228 B AREA PINNED · ETAG LOCKED</span>
            </div>
            <WidgetFrame
              src="./widgets/entries.html"
              title="Perga pinned memory entries"
              data={MEMORY_PINNED_DATA}
              initialHeight={240}
              onIntent={handleWidgetIntent}
            />
            <div className="site-memory-divider" />
            <WidgetFrame
              src="./widgets/entries.html"
              title="Perga topic memory entries"
              data={MEMORY_TOPICS_DATA}
              initialHeight={200}
              onIntent={handleWidgetIntent}
            />
          </div>
        </section>

        {/* 4. QUIET LOCAL SPECS + CLOSING */}
        <section className="site-facts">
          <div>
            <span>WORKSPACES</span>
            <strong>Worktree · Jujutsu <code>jj</code> · Linux OverlayFS · SSH</strong>
          </div>
          <div>
            <span>TERMINALS</span>
            <strong>Persistent Rust PTY (<code>#![forbid(unsafe_code)]</code>)</strong>
          </div>
          <div>
            <span>STORAGE</span>
            <strong>Local SQLite WAL + UTF-8 Markdown</strong>
          </div>
          <div>
            <span>PROTOCOLS</span>
            <strong>Zero-template start · 27–40 line <code>.perga.js</code></strong>
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
              <a className="site-closing-btn-outline" href="https://github.com/keanji-x/perga/blob/main/docs/product/vision.en.md" target="_blank" rel="noreferrer">Vision</a>
            </div>
          </div>
        </section>
      </main>

      <footer className="site-footer">
        <div className="site-footer-inner">
          <span>PERGA · APACHE 2.0</span>
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
