import { useCallback, useEffect, useRef, useState } from 'react';

type WidgetFrameProps = Readonly<{
  src: string;
  title: string;
  data: unknown;
  initialHeight?: number;
  onIntent?: (intent: Record<string, unknown>) => void;
}>;

function readPergaTheme() {
  const styles = getComputedStyle(document.documentElement);
  const pick = (prop: string, fallback: string) => styles.getPropertyValue(prop).trim() || fallback;
  // The landing page always renders in Perga's light warm-paper scheme.
  // Keep widget iframes in the matching light color-scheme so browsers never
  // paint an opaque dark canvas behind transparent iframe content when the OS
  // prefers dark mode.
  return {
    type: 'perga:theme',
    scheme: 'light',
    tokens: {
      bg: pick('--paper', '#fdfcfa'),
      paper: pick('--paper', '#fdfcfa'),
      surface: pick('--bg', '#f6f4ee'),
      text: pick('--text', '#1e2026'),
      text2: pick('--text-2', '#525763'),
      text3: pick('--text-3', '#868b98'),
      hairline: pick('--hairline', '#d8d4cb'),
      accent: pick('--accent', '#3955b8'),
      fontSans: pick('--font-sans', 'Inter, -apple-system, sans-serif'),
      fontSerif: pick('--font-serif', 'Newsreader, Georgia, serif'),
      fontMono: pick('--font-mono', '"JetBrains Mono", ui-monospace, monospace'),
    },
  };
}

export function WidgetFrame({
  src,
  title,
  data,
  initialHeight = 140,
  onIntent,
}: WidgetFrameProps) {
  const iframeRef = useRef<HTMLIFrameElement | null>(null);
  const [height, setHeight] = useState(initialHeight);

  const pushData = useCallback(() => {
    const target = iframeRef.current?.contentWindow;
    if (!target) return;
    target.postMessage(readPergaTheme(), '*');
    target.postMessage({ type: 'perga:data', data }, '*');
  }, [data]);

  useEffect(() => {
    const onMessage = (ev: MessageEvent) => {
      if (ev.source !== iframeRef.current?.contentWindow) return;
      const msg = ev.data;
      if (!msg || typeof msg !== 'object') return;
      if (msg.type === 'perga:ready') {
        pushData();
      } else if (msg.type === 'perga:resize' && typeof msg.height === 'number' && Number.isFinite(msg.height)) {
        setHeight(Math.max(88, Math.min(900, Math.ceil(msg.height))));
      } else if (msg.type === 'perga:intent' && onIntent) {
        onIntent(msg as Record<string, unknown>);
      }
    };

    window.addEventListener('message', onMessage);
    // Also push immediately in case the iframe loaded before this effect attached.
    pushData();
    return () => {
      window.removeEventListener('message', onMessage);
    };
  }, [pushData, onIntent]);

  return (
    <iframe
      ref={iframeRef}
      src={src}
      title={title}
      className="site-widget-frame"
      style={{ height: `${height}px` }}
      sandbox="allow-scripts"
      scrolling="no"
      onLoad={pushData}
    />
  );
}
