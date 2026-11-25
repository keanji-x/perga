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
  const dark = window.matchMedia('(prefers-color-scheme: dark)').matches;
  const pick = (prop: string, fallback: string) => styles.getPropertyValue(prop).trim() || fallback;
  return {
    type: 'perga:theme',
    scheme: dark ? 'dark' : 'light',
    tokens: {
      bg: pick('--paper', dark ? '#181b21' : '#fdfcfa'),
      paper: pick('--paper', dark ? '#181b21' : '#fdfcfa'),
      surface: pick('--bg', dark ? '#20242c' : '#f6f4ee'),
      text: pick('--text', dark ? '#eef0f4' : '#1e2026'),
      text2: pick('--text-2', dark ? '#b5bac7' : '#525763'),
      text3: pick('--text-3', dark ? '#828898' : '#868b98'),
      hairline: pick('--hairline', dark ? '#363c49' : '#d8d4cb'),
      accent: pick('--accent', dark ? '#8ca8ff' : '#3955b8'),
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

    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const onSchemeChange = () => {
      iframeRef.current?.contentWindow?.postMessage(readPergaTheme(), '*');
    };

    window.addEventListener('message', onMessage);
    media.addEventListener('change', onSchemeChange);
    // Also push immediately in case the iframe loaded before this effect attached.
    pushData();
    return () => {
      window.removeEventListener('message', onMessage);
      media.removeEventListener('change', onSchemeChange);
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
