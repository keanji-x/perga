import { FOLLOW_DEFAULT_LABEL } from './model-selection.ts';

import * as RadixSlider from '@radix-ui/react-slider';

import styles from './model-pill.module.css';

export function EffortSlider({
  stops, selectedIndex, isDisabled, onChangeIndex,
}: Readonly<{
  stops: readonly Readonly<{ value: string | null; label: string }>[];
  selectedIndex: number;
  isDisabled: boolean;
  onChangeIndex: (index: number) => void;
}>) {
  const segments = Math.max(1, stops.length - 1);

  return (
    <RadixSlider.Root
      className={styles.radixSliderRoot}
      min={0}
      max={segments}
      step={1}
      value={[selectedIndex]}
      disabled={isDisabled || stops.length <= 1}
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
            ? '6px'
            : idx === segments
              ? 'calc(100% - 6px)'
              : `${(idx / segments) * 100}%`;
          return (
            <span
              key={stop.value ?? idx}
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
        aria-disabled={isDisabled || stops.length <= 1}
        aria-valuetext={stops.at(selectedIndex)?.label ?? FOLLOW_DEFAULT_LABEL}
      />
    </RadixSlider.Root>
  );
}
