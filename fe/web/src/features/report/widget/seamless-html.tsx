// Scheme B HTML Graphic Widget host:
// Runs inside `sandbox="allow-scripts"` (opaque origin, zero cookies/API access) and speaks
// the closed 4-message Perga Bridge:
//   Host -> Widget: `perga:data`, `perga:theme`
//   Widget -> Host: `perga:resize`, `perga:intent` (`open_file` | `open_session`, read-only)

import { useCallback, useEffect, useRef } from 'react';
import { parseWorkspaceRelativeFilePath } from '../../../../../core/domain/report-file.ts';
import { WIDGET_MAX_HEIGHT, WIDGET_MIN_HEIGHT } from '../../../../../core/domain/report-widget.ts';
import { useState } from '../../../ui/state/public.ts';
import styles from './widget.module.css';

const HTML_WIDGET_SANDBOX = 'allow-scripts';

type PergaThemePayload = Readonly<{
  scheme: 'light' | 'dark';
  tokens: Readonly<Record<string, string>>;
}>;

function readHostTheme(): PergaThemePayload {
  if (typeof window === 'undefined' || typeof document === 'undefined') {
    return { scheme: 'light', tokens: {} };
  }
  const root = document.documentElement;
  const computed = window.getComputedStyle(root);
  const readVar = (name: string): string => computed.getPropertyValue(name).trim();
  const dataTheme = root.getAttribute('data-theme') ?? '';
  const isDark = dataTheme === 'dark'
    || root.classList.contains('dark')
    || (dataTheme === '' && window.matchMedia?.('(prefers-color-scheme: dark)').matches);

  return {
    scheme: isDark ? 'dark' : 'light',
    tokens: {
      bg: readVar('--bg') || (isDark ? '#24272e' : '#f4f2ec'),
      paper: readVar('--paper') || readVar('--bg') || (isDark ? '#2c3039' : '#fdfcfa'),
      surface: readVar('--paper') || (isDark ? '#2c3039' : '#ffffff'),
      text: readVar('--text') || (isDark ? '#eef0f4' : '#1e2026'),
      text2: readVar('--text-2') || (isDark ? '#b5bac7' : '#525763'),
      text3: readVar('--text-3') || (isDark ? '#828898' : '#868b98'),
      hairline: readVar('--hairline') || (isDark ? '#3b404d' : '#e4e0d8'),
      accent: readVar('--accent') || (isDark ? '#86a0ff' : '#3955b8'),
      fontSans: readVar('--font-sans'),
      fontSerif: readVar('--font-serif'),
      fontMono: readVar('--font-mono'),
    },
  };
}

export function SeamlessHtmlWidget({
  html,
  title,
  data,
  chrome = 'seamless',
  initialHeight = 140,
  selectedId,
  selectionKey,
  onSelect,
  onOpenFile,
  onOpenWorker,
}: Readonly<{
  html: string;
  title: string;
  data: unknown;
  chrome?: 'seamless' | 'framed';
  initialHeight?: number;
  selectedId?: string;
  selectionKey?: string;
  onSelect?: (key: string, value: string) => void;
  onOpenFile?: (path: string) => void;
  onOpenWorker?: (sessionId: string) => void;
}>) {
  const frameRef = useRef<HTMLIFrameElement | null>(null);
  const [height, setHeight] = useState<number>(initialHeight);
  const [scheme, setScheme] = useState<'light' | 'dark'>(() => readHostTheme().scheme);

  const pushState = useCallback((targetWindow: Window | null | undefined) => {
    const theme = readHostTheme();
    setScheme(theme.scheme);
    if (targetWindow == null) return;
    targetWindow.postMessage({ type: 'perga:theme', ...theme }, '*');
    targetWindow.postMessage({ type: 'perga:data', data, selectedId }, '*');
  }, [data, selectedId]);

  useEffect(() => {
    pushState(frameRef.current?.contentWindow);
  }, [pushState, html]);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof document === 'undefined') return undefined;

    const handleMessage = (event: MessageEvent<unknown>) => {
      const frameWindow = frameRef.current?.contentWindow;
      if (frameWindow == null || event.source !== frameWindow) return;
      const msg = event.data;
      if (msg === null || typeof msg !== 'object') return;
      const record = msg as Record<string, unknown>;
      const type = record.type;

      if (type === 'perga:ready') {
        pushState(frameWindow);
        return;
      }

      if (type === 'perga:resize' && typeof record.height === 'number' && Number.isFinite(record.height)) {
        const clamped = Math.max(WIDGET_MIN_HEIGHT, Math.min(WIDGET_MAX_HEIGHT, Math.ceil(record.height)));
        setHeight(clamped);
        return;
      }

      if (type === 'perga:intent') {
        const kind = record.kind;
        if (kind === 'open_file' && typeof record.path === 'string' && record.path.trim() !== '') {
          const parsed = parseWorkspaceRelativeFilePath(record.path.trim());
          if (parsed !== null) {
            onOpenFile?.(parsed.path);
          }
        } else if (kind === 'open_session' && typeof record.sessionId === 'string' && record.sessionId.trim() !== '') {
          onOpenWorker?.(record.sessionId.trim());
        } else if (kind === 'select' && typeof record.value === 'string') {
          const val = record.value.trim();
          const key = typeof record.key === 'string' && record.key.trim() !== ''
            ? record.key.trim()
            : (selectionKey ?? 'default');
          if (val !== '' && val.length <= 128 && key.length <= 64) {
            onSelect?.(key, val);
          }
        }
      }
    };

    window.addEventListener('message', handleMessage);

    const observer = new MutationObserver(() => {
      pushState(frameRef.current?.contentWindow);
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'class'] });

    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    const onSchemeChange = () => { pushState(frameRef.current?.contentWindow); };
    media?.addEventListener?.('change', onSchemeChange);

    return () => {
      window.removeEventListener('message', handleMessage);
      observer.disconnect();
      media?.removeEventListener?.('change', onSchemeChange);
    };
  }, [pushState, selectionKey, onSelect, onOpenFile, onOpenWorker]);

  return (
    <figure className={styles.host}>
      <iframe
        ref={frameRef}
        className={chrome === 'framed' ? styles.framedFrame : styles.seamlessFrame}
        srcDoc={html}
        title={title}
        style={{ blockSize: `${height}px`, colorScheme: scheme }}
        sandbox={HTML_WIDGET_SANDBOX}
        referrerPolicy="no-referrer"
        onLoad={() => { pushState(frameRef.current?.contentWindow); }}
      />
    </figure>
  );
}
