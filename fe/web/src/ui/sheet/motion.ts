import { playSpring, playTrajectory, springTrajectory, type SpringPlayback, type SpringSample } from '../motion/spring.ts';
import { createPlaybackTarget } from '../motion/target.ts';
import { notifyAncestorVisualChange } from '../hover-preview/visual-change.ts';

/** One paint writer and playback identity per connected panel lifetime. Layout
 * changes retain q/v; q is motion relative to the declared unanimated frame. */
export function createSheetMotion(panel: HTMLDivElement, getFooter: () => HTMLDivElement | null) {
  const active = createPlaybackTarget<number>();
  let footerPlayback: SpringPlayback | null = null;
  let footerPainted = 0;
  let live = true;
  let revision = 0;
  // Translation leaves border-box height invariant in the declared unscaled
  // CSS-pixel frame. Keep fractional travel; offsetHeight would round it.
  let painted: SpringSample | null = null;
  const paint = (value: number) => ({ transform: `translateY(${value}px)` });
  const writeFooter = (value: number) => {
    const footer = getFooter();
    if (footer !== null) footer.style.transform = value === 0 ? '' : paint(value).transform;
  };
  const cancelFooter = () => {
    footerPlayback?.cancel(); footerPlayback = null;
  };
  const holdFooter = (): SpringSample => {
    const sample = footerPlayback?.sample();
    if (sample !== undefined) {
      footerPainted = Math.max(0, sample.value);
      writeFooter(footerPainted);
    }
    cancelFooter();
    return { value: footerPainted, velocity: sample?.velocity ?? 0 };
  };
  const clearFooter = () => {
    cancelFooter();
    footerPainted = 0; writeFooter(0);
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
      const footerFrom = holdFooter();
      const from = hold(); const generation = revision;
      const footer = getFooter();
      const footerStart = footer?.isConnected ? footerFrom.value : 0;
      const complete = () => {
        if (!live || revision !== generation || !panel.isConnected) return;
        clearFooter();
        painted = { value: to, velocity: 0 }; write(to); settled();
      };
      if (immediate || exit && from.value >= to || from.value === to && from.velocity === 0 && footerStart === 0) {
        complete(); return true;
      }
      const footerReturn = !exit && footerStart > 0 ? springTrajectory(footerStart, 0, footerFrom.velocity) : undefined;
      const playback = playSpring([panel], from.value, to, from.velocity, paint, { stopAtTarget: exit, minimumDuration: footerReturn?.duration });
      // Only the panel's finished promise owns settlement, including manual finish().
      active.replace({ ...playback, cancel: () => { playback.cancel(); cancelFooter(); } }, complete);
      if (exit && footer?.isConnected) {
        const exitTrajectory = springTrajectory(from.value, to, from.velocity, { stopAtTarget: true });
        const panelSpan = to - from.value;
        const exitScale = Math.max(panelSpan,
          (footer.ownerDocument.defaultView?.innerHeight ?? 0) - footer.getBoundingClientRect().top) / panelSpan;
        footerPlayback = playTrajectory([footer], {
          duration: exitTrajectory.duration,
          sample: time => {
            const sample = exitTrajectory.sample(time);
            return { value: Math.max(0, footerFrom.value + (sample.value - from.value) * exitScale), velocity: sample.velocity * exitScale };
          },
          scaleVelocity: (value, rate) => value * rate,
        }, paint);
      } else if (footer?.isConnected && footerReturn) {
        footerPlayback = playTrajectory([footer], { ...footerReturn, scaleVelocity: (value, rate) => value * rate },
          value => paint(Math.max(0, value)));
      }
      if (footerPlayback !== null) {
        footerPlayback.animations[0].startTime = playback.animations[0].startTime;
        void footerPlayback.finished.catch(() => {});
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
