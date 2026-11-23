import { useRef, type KeyboardEvent, type PointerEvent } from 'react';
import { useState } from '../../../ui/state/public.ts';
import styles from './model-pill.module.css';

export type EffortStop = Readonly<{ value: string; label: string }>;

/**
 * Stepped left-to-right intensity meter for reasoning effort.
 * Stops contain only real ordered levels (low -> high); inherited default is
 * reflected in a calmer neutral tone on its actual step while explicit overrides
 * light up in accent. Clicking the currently pinned step again resets to default.
 */
export function EffortSlider({
  stops,
  selectedIndex,
  defaultIndex,
  valueText,
  isDisabled,
  onChangeIndex,
  onResetToDefault,
  onHoverChange,
}: Readonly<{
  stops: readonly EffortStop[];
  /** Index of the explicitly selected effort, or -1 when following default / unavailable. */
  selectedIndex: number;
  /** Index of the model's advertised default effort, or -1 when unknown. */
  defaultIndex: number;
  /** Accessible announcement for the current state (e.g. "high", "Default (high)", "Default", "N/A"). */
  valueText: string;
  isDisabled: boolean;
  onChangeIndex: (index: number) => void;
  onResetToDefault?: () => void;
  onHoverChange?: (index: number | null) => void;
}>) {
  const trackRef = useRef<HTMLDivElement | null>(null);
  const draggingRef = useRef(false);
  const draggedIndexRef = useRef<number | null>(null);
  const startSelectedRef = useRef<number>(-1);
  const movedAcrossStepsRef = useRef(false);
  const [hoverIndex, setHoverIndex] = useState<number | null>(null);

  const count = stops.length;
  const interactive = !isDisabled && count > 0;
  const isFollowingDefault = selectedIndex === -1 && defaultIndex >= 0;
  const activeIndex = selectedIndex >= 0 ? selectedIndex : defaultIndex;
  const previewIndex = interactive && hoverIndex !== null ? hoverIndex : null;

  const setHovered = (next: number | null) => {
    setHoverIndex(next);
    onHoverChange?.(next);
  };

  const indexFromClientX = (clientX: number): number => {
    const rect = trackRef.current?.getBoundingClientRect();
    if (rect === undefined || rect.width <= 0 || count <= 1) return 0;
    const ratio = (clientX - rect.left) / rect.width;
    return Math.min(count - 1, Math.max(0, Math.floor(ratio * count)));
  };

  const handlePointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (!interactive || event.button !== 0) return;
    event.preventDefault();
    draggingRef.current = true;
    movedAcrossStepsRef.current = false;
    startSelectedRef.current = selectedIndex;
    event.currentTarget.focus();
    event.currentTarget.setPointerCapture?.(event.pointerId);
    const idx = indexFromClientX(event.clientX);
    draggedIndexRef.current = idx;
    setHovered(idx);
    if (idx !== selectedIndex) onChangeIndex(idx);
  };

  const handlePointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (!interactive) return;
    const idx = indexFromClientX(event.clientX);
    setHovered(idx);
    if (draggingRef.current && idx !== draggedIndexRef.current) {
      movedAcrossStepsRef.current = true;
      draggedIndexRef.current = idx;
      onChangeIndex(idx);
    }
  };

  const handlePointerUp = (event: PointerEvent<HTMLDivElement>) => {
    if (!draggingRef.current) return;
    draggingRef.current = false;
    const releasedIdx = draggedIndexRef.current;
    draggedIndexRef.current = null;
    if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    const rect = trackRef.current?.getBoundingClientRect();
    const outsideTrack = rect === undefined
      || event.clientX < rect.left || event.clientX > rect.right
      || event.clientY < rect.top || event.clientY > rect.bottom;
    if (event.pointerType !== 'mouse' || outsideTrack) {
      setHovered(null);
    }
    // A clean click on an already-pinned step toggles back to following default.
    if (!movedAcrossStepsRef.current && releasedIdx !== null
      && releasedIdx === startSelectedRef.current && onResetToDefault !== undefined) {
      onResetToDefault();
    }
  };

  const handlePointerCancel = (event: PointerEvent<HTMLDivElement>) => {
    draggingRef.current = false;
    draggedIndexRef.current = null;
    if (event.currentTarget.hasPointerCapture?.(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    setHovered(null);
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (isDisabled) return;
    if ((event.key === 'Backspace' || event.key === 'Delete') && onResetToDefault !== undefined) {
      event.preventDefault();
      onResetToDefault();
      return;
    }
    if (count === 0) return;
    const current = activeIndex;
    if (event.key === 'ArrowRight' || event.key === 'ArrowUp') {
      event.preventDefault();
      onChangeIndex(current < 0 ? 0 : Math.min(count - 1, current + 1));
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') {
      event.preventDefault();
      onChangeIndex(current <= 0 ? 0 : current - 1);
    } else if (event.key === 'Home') {
      event.preventDefault();
      onChangeIndex(0);
    } else if (event.key === 'End') {
      event.preventDefault();
      onChangeIndex(count - 1);
    }
  };

  const displayCount = Math.max(1, count);

  return (
    <div
      ref={trackRef}
      role="slider"
      tabIndex={isDisabled ? -1 : 0}
      aria-label="Reasoning effort"
      aria-valuemin={0}
      aria-valuemax={Math.max(0, count - 1)}
      aria-valuenow={activeIndex >= 0 ? activeIndex : 0}
      aria-valuetext={valueText}
      aria-disabled={isDisabled || count === 0 ? 'true' : undefined}
      data-nc-disabled={isDisabled || count === 0 ? '' : undefined}
      className={styles.effortMeter}
      onPointerDown={handlePointerDown}
      onPointerMove={handlePointerMove}
      onPointerUp={handlePointerUp}
      onPointerCancel={handlePointerCancel}
      onPointerLeave={() => { if (!draggingRef.current) setHovered(null); }}
      onKeyDown={handleKeyDown}
    >
      {Array.from({ length: displayCount }, (_, idx) => {
        const isActiveFill = count > 0 && activeIndex >= 0 && idx <= activeIndex;
        const isPreviewFill = previewIndex !== null && idx <= previewIndex;
        const isDefaultMarker = count > 0 && defaultIndex === idx && selectedIndex >= 0 && selectedIndex !== defaultIndex;
        const state = isPreviewFill
          ? 'preview'
          : isActiveFill
            ? (isFollowingDefault ? 'default' : 'active')
            : 'empty';
        return (
          <span
            key={stops[idx]?.value ?? idx}
            className={styles.effortSegmentSlot}
            data-nc-step={idx}
          >
            <span
              className={styles.effortSegment}
              data-nc-state={state}
              aria-hidden="true"
            />
            {isDefaultMarker && <span className={styles.effortDefaultDot} aria-hidden="true" />}
          </span>
        );
      })}
    </div>
  );
}
