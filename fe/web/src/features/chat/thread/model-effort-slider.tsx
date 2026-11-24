import * as RadixSlider from '@radix-ui/react-slider';
import { FOLLOW_DEFAULT_LABEL } from './model-selection.ts';
import styles from './model-pill.module.css';

export type EffortStop = Readonly<{ value: string | null; label: string }>;

/**
 * Editorial stepped reasoning-effort slider built on `@radix-ui/react-slider`.
 * Stop 0 is always Default (`value: null`), so Home cleanly returns to Default
 * and End jumps to the highest supported effort.
 */
export function EffortSlider({
  stops,
  selectedIndex,
  valueText,
  isDisabled,
  onChangeIndex,
}: Readonly<{
  stops: readonly EffortStop[];
  selectedIndex: number;
  valueText: string;
  isDisabled: boolean;
  onChangeIndex: (index: number) => void;
}>) {
  const segments = Math.max(1, stops.length - 1);
  const disabled = isDisabled || stops.length <= 1;

  return (
    <RadixSlider.Root
      className={styles.radixSliderRoot}
      min={0}
      max={segments}
      step={1}
      value={[selectedIndex]}
      disabled={disabled}
      aria-label="Reasoning effort"
      onValueChange={([idx]) => {
        if (idx === undefined) return;
        onChangeIndex(idx);
      }}
      onValueCommit={([idx]) => {
        if (idx === undefined) return;
        onChangeIndex(idx);
      }}
    >
      <RadixSlider.Track className={styles.radixSliderTrack}>
        <RadixSlider.Range className={styles.radixSliderRange} />
        {stops.length > 1 && stops.map((stop, idx) => {
          const tickPos = idx === 0
            ? '5px'
            : idx === segments
              ? 'calc(100% - 5px)'
              : `${(idx / segments) * 100}%`;
          return (
            <span
              key={stop.value ?? 'default'}
              className={styles.radixSliderTick}
              data-nc-filled={idx <= selectedIndex ? 'true' : 'false'}
              style={{ insetInlineStart: tickPos }}
            />
          );
        })}
      </RadixSlider.Track>
      <RadixSlider.Thumb
        className={styles.radixSliderThumb}
        aria-label="Reasoning effort"
        aria-disabled={disabled}
        aria-valuetext={valueText || (stops.at(selectedIndex)?.label ?? FOLLOW_DEFAULT_LABEL)}
      />
    </RadixSlider.Root>
  );
}
