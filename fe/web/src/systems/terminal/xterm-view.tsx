// Terminal protocol, rendering and connection lifecycle. Reconnect replaces only the WebSocket.
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import { forwardRef,useEffect,useImperativeHandle,useRef } from 'react';
import { DARK_TERMINAL_THEME as DARK_THEME,LIGHT_TERMINAL_THEME as LIGHT_THEME,TERMINAL_FONT_SIZE } from '../../styles/theme-values.ts';
import { ErrorBox } from '../../ui/error-box/public.tsx';
import { useState } from '../../ui/state/public.ts';
import { MONO_STACK } from './font-stack.ts';
import {
copyTextToClipboard,
createOsc52Handler,
osc52HostMayWrite,
} from './osc52.ts';
import {
decodeTerminalServerFrame,
encodeTerminalInputFrame,
encodeTerminalResizeFrame,
terminalWsUrl,
} from './protocol.ts';
import {
createXtermWheelTarget,
type XtermWheelTarget,
} from './xterm-adapter.ts';

/** Child exit info from the daemon; `exit_code` and `signal_killed` are mutually exclusive at the source. `null` clears a prior badge. */
export interface ExitChange {
  exit_code: number | null;
  signal_killed: boolean;
}

interface XtermViewProps {
  terminalId: string;
  theme?: 'light' | 'dark';
  /** The daemon-assigned role, or `null` on reconnect/disconnect so the parent can clear a badge. */
  onRoleChange?: (role: 'Owner' | null) => void;
  /** Child-exit info lifted to the parent; idempotent across the exit frame and WS close. `null` on reconnect. */
  onExitChange?: (exit: ExitChange | null) => void;
  onStatusChange?: (status: TerminalConnectionStatus) => void;
  /** The view stays mounted after first open; hidden cards must not resize or accept OSC 52. */
  visible?: boolean;
  focused?: boolean;
}

export interface XtermViewHandle {
  refresh(): void;
  getWheelTarget(): XtermWheelTarget | null;
}

/** Last close info, surfaced in the disconnected overlay. */
interface CloseInfo {
  code: number;
  reason: string;
}

// Four-plus rows/cols worth of host surface avoids xterm/FitAddon
// bootstrapping against a collapsed card body.
const MIN_MOUNT_WIDTH_PX = 80;
const MIN_MOUNT_HEIGHT_PX = 24;
// Below 8x4 is not a usable PTY viewport; suppress resize frames
// from transient or failed fits so they cannot poison the shared model.
const MIN_COMMIT_COLS = 8;
const MIN_COMMIT_ROWS = 4;

function isNonDegenerateMountSize(width: number, height: number): boolean {
  return width >= MIN_MOUNT_WIDTH_PX && height >= MIN_MOUNT_HEIGHT_PX;
}

/** UI status for the terminal protocol. */
export type TerminalConnectionStatus =
  | 'connecting'
  | 'connected'
  | 'closed'
  | 'exited';

/** Perga binary terminal WebSocket bridge. */
export const XtermView = forwardRef<XtermViewHandle, XtermViewProps>(function XtermView({
  terminalId,
  theme = 'light',
  onRoleChange,
  onExitChange,
  onStatusChange,
  visible = true,
  focused = false,
}, ref) {
  const rootRef = useRef<HTMLDivElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  // Live ref so the theme effect can re-theme without tearing down the WebSocket.
  const termRef = useRef<Terminal | null>(null);
  // The bridge-mount effect omits `theme` from its deps on purpose; it reads the current value here.
  const latestThemeRef = useRef<'light' | 'dark'>(theme);
  latestThemeRef.current = theme;
  const focusedRef = useRef(focused);
  focusedRef.current = focused;
  const focusOrigin = useRef(document.activeElement);
  const visibleRef = useRef(visible);
  visibleRef.current = visible;
  useEffect(() => {
    if (visible && focused) termRef.current?.focus();
  }, [visible, focused]);
  const flushResizeRef = useRef<(() => void) | null>(null);
  useEffect(() => {
    if (visible) flushResizeRef.current?.();
  }, [visible]);
  const [status, setStatus] = useState<TerminalConnectionStatus>('connecting');
  const onStatusChangeRef = useRef(onStatusChange);
  onStatusChangeRef.current = onStatusChange;
  useEffect(() => { onStatusChangeRef.current?.(status); }, [status]);
  const [closeInfo, setCloseInfo] = useState<CloseInfo | null>(null);
  const exitedRef = useRef(false);
  // Captured in a ref so a callback identity flip from the parent does not tear down the WebSocket.
  const onRoleChangeRef = useRef<XtermViewProps['onRoleChange']>(onRoleChange);
  onRoleChangeRef.current = onRoleChange;
  const onExitChangeRef = useRef<XtermViewProps['onExitChange']>(onExitChange);
  onExitChangeRef.current = onExitChange;
  // Reconnect only the browser transport. The mounted xterm and its output
  // stay available until a successful attach supplies an authoritative replay.
  const reconnectRef = useRef<(() => void) | null>(null);
  useImperativeHandle(
    ref,
    () => ({
      refresh: () => reconnectRef.current?.(),
      getWheelTarget: () => {
        if (!rootRef.current) {
          return null;
        }
        return createXtermWheelTarget({
          root: rootRef.current,
          terminalRef: termRef,
        });
      },
    }),
    [],
  );
  const [layoutRetryKey, setLayoutRetryKey] = useState(0);
  const [geometryDeferred, setGeometryDeferred] = useState(false);
  const lastFailedMountSizeRef = useRef<{ w: number; h: number } | null>(null);

  // Apply themes locally without reconnecting the transport.
  useEffect(() => {
    const term = termRef.current;
    if (term) {
      term.options.theme = theme === 'dark' ? DARK_THEME : LIGHT_THEME;
    }
  }, [theme]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const deferUntilUsableGeometry = () => {
      setGeometryDeferred(true);
      const ro = new ResizeObserver(() => {
        const nextWidth = container.offsetWidth;
        const nextHeight = container.offsetHeight;
        const lastFailedMountSize = lastFailedMountSizeRef.current;
        if (
          lastFailedMountSize &&
          nextWidth === lastFailedMountSize.w &&
          nextHeight === lastFailedMountSize.h
        ) {
          return;
        }
        if (!isNonDegenerateMountSize(nextWidth, nextHeight)) return;
        ro.disconnect();
        setLayoutRetryKey((k) => k + 1);
      });
      ro.observe(container);
      return () => {
        ro.disconnect();
      };
    };
    const removeTestDumpHook = () => {
      if (typeof window === 'undefined') return;
      const w = window as unknown as {
        __xtermDumps__?: Record<string, () => string>;
      };
      if (w.__xtermDumps__) delete w.__xtermDumps__[terminalId];
    };
    const mountWidth = container.offsetWidth;
    const mountHeight = container.offsetHeight;
    if (!isNonDegenerateMountSize(mountWidth, mountHeight)) {
      lastFailedMountSizeRef.current = { w: mountWidth, h: mountHeight };
      return deferUntilUsableGeometry();
    }
    setGeometryDeferred(false);

    const term = new Terminal({
      theme:
        latestThemeRef.current === 'dark' ? DARK_THEME : LIGHT_THEME,
      fontFamily: MONO_STACK,
      fontSize: TERMINAL_FONT_SIZE,
      // Bound the local xterm scrollback retained for this terminal view.
      scrollback: 2000,
      convertEol: true,
      allowProposedApi: true,
      cursorBlink: true,
      disableStdin: true,
    });
    termRef.current = term;
    // OSC-echo e2e instrumentation, gated on `?testMounts=1`: a per-terminal buffer serializer, read from the buffer because the canvas renderer mirrors no glyphs into the DOM.
    if (typeof window !== 'undefined') {
      const url = new URL(window.location.href);
      if (url.searchParams.get('testMounts') === '1') {
        const w = window as unknown as {
          __xtermDumps__?: Record<string, () => string>;
        };
        const dumps = (w.__xtermDumps__ ??= {});
        dumps[terminalId] = () => {
          const buf = term.buffer.active;
          const lines: string[] = [];
          for (let i = 0; i < buf.length; i += 1) {
            const line = buf.getLine(i);
            if (line) lines.push(line.translateToString(true));
          }
          return lines.join('\n');
        };
      }
    }
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(container);
    term.element?.setAttribute('data-nc-xterm', '');
    // Suppress xterm.js's OSC 10/11/12 auto-reply: the daemon is the sole responder, and xterm's transparent `clearColor` would race back as pure white.
    term.parser.registerOscHandler(10, () => true);
    term.parser.registerOscHandler(11, () => true);
    term.parser.registerOscHandler(12, () => true);
    term.parser.registerOscHandler(
      52,
      createOsc52Handler(copyTextToClipboard, () =>
        osc52HostMayWrite(container, visibleRef.current),
      ),
    );
    // VS Code's default Mac sendSequence keybindings (Cmd+Left/Right/Backspace → ^A/^E/^U); pure-Cmd only.
    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== 'keydown') return true;
      if (e.isComposing) return true;
      // Shift+Tab returns to the host controls; bare Tab remains shell completion.
      if (e.key === 'Tab' && e.shiftKey) return false;
      if (e.key === 'Escape' || (e.ctrlKey && e.key.toLowerCase() === 'c')) e.stopPropagation();
      // xterm.js sends CR for both Enter and Shift+Enter. This host maps
      // Shift+Enter to ESC CR (the chord hosted TUIs use for a newline).
      if (
        e.key === 'Enter' &&
        e.shiftKey &&
        !e.ctrlKey &&
        !e.metaKey &&
        !e.altKey
      ) {
        term.input('\x1b\r', true);
        e.preventDefault();
        return false;
      }
      if (!e.metaKey || e.ctrlKey || e.altKey) return true;
      if (e.key === 'ArrowLeft') {
        term.input('\x01', true);
        e.preventDefault();
        return false;
      }
      if (e.key === 'ArrowRight') {
        term.input('\x05', true);
        e.preventDefault();
        return false;
      }
      if (e.key === 'Backspace') {
        term.input('\x15', true);
        e.preventDefault();
        return false;
      }
      return true;
    });
    // Reachable by Tab; Shift+Tab exits to the host while bare Tab stays shell completion.
    const helperTextarea = container.querySelector<HTMLTextAreaElement>(
      '.xterm-container textarea.xterm-helper-textarea',
    );
    if (helperTextarea) {
      helperTextarea.setAttribute('tabindex', '0');
      helperTextarea.setAttribute('data-nc-terminal-input', '');
      if (visibleRef.current && focusedRef.current && document.activeElement === focusOrigin.current) term.focus();
    }
    try {
      fit.fit();
    } catch {
      /* container may not be laid out yet on first frame */
    }
    if (term.cols < MIN_COMMIT_COLS || term.rows < MIN_COMMIT_ROWS) {
      lastFailedMountSizeRef.current = {
        w: container.offsetWidth,
        h: container.offsetHeight,
      };
      term.dispose();
      removeTestDumpHook();
      if (termRef.current === term) termRef.current = null;
      return deferUntilUsableGeometry();
    }
    lastFailedMountSizeRef.current = null;

    const connect = () => {
      setStatus('connecting');
      setCloseInfo(null);
      exitedRef.current = false;
      onExitChangeRef.current?.(null);
      term.options.disableStdin = true;
      let live = true;
      let connectionReady = false;
      const scopedUrl = terminalWsUrl(terminalId, location);
      const ws = new WebSocket(scopedUrl);
      ws.binaryType = 'arraybuffer';
      const sendBinary = (frame: Uint8Array) => {
        if (!live) return;
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(frame);
        }
      };

      const mountDesired = { cols: term.cols, rows: term.rows };
      let lastCols = term.cols;
      let lastRows = term.rows;

      ws.onopen = () => {
        if (!live) { ws.close(); return; }
        onRoleChangeRef.current?.('Owner');
        setStatus('connected');
        connectionReady = true;
        term.options.disableStdin = false;
        term.reset();
        if (mountDesired.cols >= MIN_COMMIT_COLS && mountDesired.rows >= MIN_COMMIT_ROWS) {
          lastCols = mountDesired.cols;
          lastRows = mountDesired.rows;
          sendBinary(encodeTerminalResizeFrame(mountDesired.cols, mountDesired.rows));
        }
      };

      ws.onmessage = (e) => {
        if (!live) return;
        if (e.data instanceof ArrayBuffer || e.data instanceof Uint8Array) {
          const frame = decodeTerminalServerFrame(e.data);
          if (frame.kind === 'output') {
            term.write(frame.bytes);
            return;
          }
          if (frame.kind === 'exit') {
            exitedRef.current = true;
            setStatus('exited');
            connectionReady = false;
            term.options.disableStdin = true;
            onExitChangeRef.current?.({
              exit_code: frame.exitCode,
              signal_killed: false,
            });
            term.writeln(
              `\r\n\x1b[2m[process exited${
                frame.exitCode != null ? ` (code ${frame.exitCode})` : ''
              }]\x1b[0m`,
            );
          }
        }
      };

      ws.onclose = (e) => {
        connectionReady = false;
        term.options.disableStdin = true;
        // 1000 + `child-exited` = daemon's clean child-exit close (mapped to `exited` even if the exit frame was dropped); 1006 abnormal; 1011 server heartbeat trip; 1001 going away.
        setCloseInfo({ code: e.code, reason: e.reason || '' });
        const isChildExitClose =
          e.code === 1000 && e.reason === 'child-exited';
        // Don't clobber a more-specific state (`exited`); a `child-exited` close promotes to `exited`.
        setStatus((prev) => {
          if (prev === 'exited') return prev;
          if (isChildExitClose) return 'exited';
          return 'closed';
        });
        // Backstop for the parent's exit badge, only when no exit frame delivered a code: the parent's callback is a plain setState and would be clobbered back to `null`.
        if (isChildExitClose && !exitedRef.current) {
          onExitChangeRef.current?.({
            exit_code: null,
            signal_killed: false,
          });
        }
        // Role is undefined once the WS is gone — parent clears any pill.
        onRoleChangeRef.current?.(null);
      };
      ws.onerror = () => {
        connectionReady = false;
        term.options.disableStdin = true;
        setStatus((prev) =>
          prev === 'exited' ? prev : 'closed',
        );
        onRoleChangeRef.current?.(null);
      };

      const dataSub = term.onData((d) => {
        if (!connectionReady) return;
        sendBinary(encodeTerminalInputFrame(d));
      });

      // Coalesce resize observations to one fit per frame to avoid pixel jitter.
      let pending = false;
      let resizeFrame: number | null = null;
      let sawInitialResizeObservation = false;
      const onResize = () => {
        // ResizeObserver always delivers an initial observation; treating it as user intent would narrow the PTY that the connection just restored.
        if (!sawInitialResizeObservation) {
          sawInitialResizeObservation = true;
          return;
        }
        if (pending) return;
        pending = true;
        resizeFrame = requestAnimationFrame(() => {
          resizeFrame = null;
          pending = false;
          if (!connectionReady) return;
          if (!visibleRef.current) return;
          // fit() mutates the local xterm in place, so a collapsed container would leave the buffer at e.g. 2x1 interpreting PTY bytes at the wrong geometry.
          if (
            !isNonDegenerateMountSize(container.offsetWidth, container.offsetHeight)
          ) {
            return;
          }
          try {
            fit.fit();
          } catch {
            return;
          }
          // fit() can still land on a marginal grid; restore last-known-good so PTY bytes don't render against it.
          if (term.cols < MIN_COMMIT_COLS || term.rows < MIN_COMMIT_ROWS) {
            if (term.cols !== lastCols || term.rows !== lastRows) {
              term.resize(lastCols, lastRows);
            }
            return;
          }
          if (term.cols !== lastCols || term.rows !== lastRows) {
            lastCols = term.cols;
            lastRows = term.rows;
            sendBinary(encodeTerminalResizeFrame(term.cols, term.rows));
          }
        });
      };
      flushResizeRef.current = onResize;
      const ro = new ResizeObserver(onResize);
      ro.observe(container);

      return () => {
        live = false;
        if (flushResizeRef.current === onResize) flushResizeRef.current = null;
        ro.disconnect();
        if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
        dataSub.dispose();
        ws.onopen = null;
        ws.onmessage = null;
        ws.onclose = null;
        ws.onerror = null;
        try {
          ws.close();
        } catch {
          /* already closed */
        }
        // Sync here rather than via onclose so a strict-mode unmount or `terminalId` change clears the parent even with no close frame.
        onRoleChangeRef.current?.(null);
      };
    };
    let disconnect = connect();
    const reconnect = () => {
      disconnect();
      disconnect = connect();
    };
    reconnectRef.current = reconnect;
    return () => {
      if (reconnectRef.current === reconnect) reconnectRef.current = null;
      disconnect();
      exitedRef.current = false;
      onExitChangeRef.current?.(null);
      term.dispose();
      removeTestDumpHook();
      if (termRef.current === term) termRef.current = null;
    };
    // Theme changes and connection status must never tear down the buffer.
  }, [terminalId, layoutRetryKey]);

  return (
    <div ref={rootRef} className="xterm-view" data-nc-terminal-id={terminalId}>
      {/* xterm hides its presentation rows itself; keep its labelled input in the accessibility tree. */}
      <div
        ref={containerRef}
        className="xterm-container"
        data-nc-terminal-viewport=""
        role="presentation"
      />
      {/* While waiting for usable layout, keep only a neutral loading state;
       *  the terminal bridge has not opened a WebSocket yet. */}
      {geometryDeferred && (
        <div className="xterm-status">Loading terminal…</div>
      )}
      {!geometryDeferred && status === 'connecting' && (
        <div className="xterm-status">connecting…</div>
      )}

      {status === 'closed' && (
        <ErrorBox
          message="Connection lost."
          description="Reconnect to the same terminal."
          actionLabel="Reconnect"
          onRetry={() => reconnectRef.current?.()}
          details={closeInfo === null ? undefined : `WebSocket ${closeInfo.code}${closeInfo.reason ? `: ${closeInfo.reason}` : ''}`}
          floating
        />
      )}

    </div>
  );
});
