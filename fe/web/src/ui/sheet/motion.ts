import { playSpring, type SpringSample } from '../motion/spring.ts';
import { createPlaybackTarget } from '../motion/target.ts';
import { notifyAncestorVisualChange } from '../hover-preview/visual-change.ts';

function footerOffset(footer: HTMLDivElement) {
  const transform = getComputedStyle(footer).transform;
  return transform === 'none' ? 0 : new DOMMatrixReadOnly(transform).m42;
}

/** One paint writer and playback identity per connected panel lifetime. Layout
 * changes retain q/v; q is motion relative to the declared unanimated frame. */
export function createSheetMotion(panel: HTMLDivElement, getFooter: () => HTMLDivElement | null) {
  const active = createPlaybackTarget<number>();
  let footerAnimation: Animation | null = null;
  let live = true;
  let revision = 0;
  // Translation leaves border-box height invariant in the declared unscaled
  // CSS-pixel frame. Keep fractional travel; offsetHeight would round it.
  let painted: SpringSample | null = null;
  const paint = (value: number) => ({ transform: `translateY(${value}px)` });
  const cancelFooter = () => {
    footerAnimation?.cancel(); footerAnimation = null;
  };
  const holdFooter = () => {
    if (footerAnimation === null) return;
    const footer = getFooter();
    if (footer !== null) {
      const offset = footerOffset(footer);
      footer.style.transform = offset === 0 ? '' : paint(offset).transform;
    }
    cancelFooter();
  };
  const clearFooter = () => {
    cancelFooter();
    const footer = getFooter();
    if (footer !== null) footer.style.transform = '';
  };
  const write = (value: number, announce = true) => {
    panel.style.transform = value === 0 ? '' : paint(value).transform;
    if (announce) notifyAncestorVisualChange(panel);
  };
  const hold = (): SpringSample => {
    const sample = active.sample() ?? painted ?? { value: panel.getBoundingClientRect().height, velocity: 0 };
    // Preserve paint before removing the native fill effect.
    write(sample.value, false); painted = sample;
    holdFooter();
    active.setOnVisualChange(undefined); active.cancel(); revision++;
    // Publish only after the old native fill can no longer override inline paint.
    notifyAncestorVisualChange(panel);
    return sample;
  };
  return {
    hold,
    direct: (sample: SpringSample) => { if (live) { painted = sample; write(sample.value); } },
    move: (to: number, immediate: boolean, settled: () => void, { exit = false } = {}): boolean => {
      // Initialization is available only after native show/layout. A hidden
      // border box (including its default zero) is never ready paint. Existing
      // q/v remains untouched while geometry is unavailable.
      if (!live || !panel.isConnected || panel.getClientRects().length === 0) return false;
      const height = panel.getBoundingClientRect().height;
      if (!Number.isFinite(height) || height <= 0 || getComputedStyle(panel).visibility !== 'visible') return false;
      const from = hold(); const generation = revision;
      const complete = () => {
        if (!live || revision !== generation || !panel.isConnected) return;
        clearFooter();
        painted = { value: to, velocity: 0 }; write(to); settled();
      };
      if (immediate || exit && from.value >= to || from.value === to && from.velocity === 0) {
        complete(); return true;
      }
      const footer = getFooter();
      const footerFrom = footer?.isConnected ? footerOffset(footer) : 0;
      const panelSpan = to - from.value;
      const syncFooter = footer?.isConnected && (exit || footerFrom > 0 && panelSpan !== 0);
      const footerTarget = exit && syncFooter ? footerFrom + Math.max(panelSpan,
        (footer.ownerDocument.defaultView?.innerHeight ?? 0) - footer.getBoundingClientRect().top) : 0;
      const keyframes: Keyframe[] = [];
      // The same samples and document clock drive the portal independently of panel clipping.
      const playback = playSpring([panel], from.value, to, from.velocity, syncFooter ? value => {
        const progress = (value - from.value) / panelSpan;
        keyframes.push(paint(Math.max(0, footerFrom + progress * (footerTarget - footerFrom))));
        return paint(value);
      } : paint, { stopAtTarget: exit });
      // Only the panel's finished promise owns settlement, including manual finish().
      active.replace({ ...playback, cancel: () => { playback.cancel(); cancelFooter(); } }, complete);
      if (syncFooter) {
        const [primary] = playback.animations;
        footerAnimation = footer.animate(keyframes, primary.effect!.getTiming());
        void footerAnimation.finished.catch(() => {});
        footerAnimation.startTime = primary.startTime;
      }
      playback.setOnVisualChange(() => {
        if (live && revision === generation && panel.isConnected) notifyAncestorVisualChange(panel);
      });
      return true;
    },
    dispose: () => { live = false; revision++; active.setOnVisualChange(undefined); active.cancel(); clearFooter(); },
  };
}
export type SheetMotion = ReturnType<typeof createSheetMotion>;
