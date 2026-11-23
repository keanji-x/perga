import { Divider } from '@astryxdesign/core/Divider';
import { DropdownMenu, DropdownMenuItem } from '@astryxdesign/core/DropdownMenu';
import { Icon as AstryxIcon } from '@astryxdesign/core/Icon';
import { Text } from '@astryxdesign/core/Text';
import { VisuallyHidden } from '@astryxdesign/core/VisuallyHidden';

import { useCallback, useEffect, useRef, type KeyboardEvent, type ReactNode, type RefObject } from 'react';

import type { PermissionModeOptionDto } from '../../../../../core/api/perga-v1.ts';
import { useState } from '../../../ui/state/public.ts';
import styles from './model-pill.module.css';

/** Keep Escape inside a host Dialog, then restore focus only while it still belongs to this pill. */
export function useEscapeFocus(open: boolean, setOpen: (open: boolean) => void, host: RefObject<HTMLSpanElement | null>) {
  const pendingFrame = useRef<number | null>(null);
  const cancelRestore = useCallback(() => {
    if (pendingFrame.current === null) return;
    cancelAnimationFrame(pendingFrame.current);
    pendingFrame.current = null;
  }, []);

  useEffect(() => cancelRestore, [cancelRestore]);
  useEffect(() => {
    if (open) cancelRestore();
  }, [open, cancelRestore]);

  const onOpenChange = (next: boolean) => {
    // Controlled Astryx layers acknowledge a close again from onHide. Keep that
    // acknowledgement from cancelling the restore for the same Escape.
    if (next !== open) cancelRestore();
    setOpen(next);
  };
  const onEscape = (event: KeyboardEvent<HTMLSpanElement>) => {
    if (event.key !== 'Escape') return;
    cancelRestore();
    if (!open) return;
    event.preventDefault();
    event.stopPropagation();
    const popup = event.target instanceof Element
      ? event.target.closest('[role="dialog"], [role="menu"]')
      : null;
    // Escape can start on the trigger inside a host Dialog. That parent
    // dialog is outside the pill and must not expand our focus boundary.
    const closingPopup = popup?.contains(event.currentTarget) ? null : popup;
    setOpen(false);
    pendingFrame.current = requestAnimationFrame(() => {
      pendingFrame.current = null;
      const active = document.activeElement;
      if (active === null || active === document.body || active === document.documentElement
        || host.current?.contains(active) || closingPopup?.contains(active)) {
        host.current?.querySelector('button')?.focus();
      }
    });
  };
  return { onEscape, onOpenChange };
}

type SettingTone = 'neutral' | 'warning' | 'danger';
type SettingOption = Readonly<{ label: string; description: string; tone?: SettingTone }>;

/** The kernel reads the mode when it starts a turn, so a change never reaches the one already running. */
export const PERMISSION_NOTE = 'Applies from the next turn.';

/** A session's approval setting: renders backend-declared modes only. */
export function PermissionModePill({ mode, options, onChange, isDisabled = false, unavailableMode, unavailableReason, placement = 'above' }: Readonly<{
  /** What the session has stored (or selected in draft). */
  mode: string;
  options: readonly PermissionModeOptionDto[];
  onChange: (mode: string) => void;
  isDisabled?: boolean;
  unavailableMode?: string;
  unavailableReason?: string;
  placement?: 'above' | 'below';
}>) {
  if (options.length === 0) return null;
  const optionMap: Readonly<Record<string, SettingOption>> = Object.fromEntries(options.map((opt) => [
    opt.id,
    { label: opt.label, description: opt.id === unavailableMode && unavailableReason !== undefined
      ? unavailableReason : opt.description,
    ...(opt.tone === 'neutral' ? {} : { tone: opt.tone }) },
  ]));
  const effectiveMode = Object.prototype.hasOwnProperty.call(optionMap, mode)
    ? mode
    : (options.find((opt) => opt.isDefault) ?? options[0]).id;
  return <SettingPill name="Approvals" options={optionMap} value={effectiveMode} onChange={onChange}
    note={PERMISSION_NOTE} isDisabled={isDisabled} unavailableOption={unavailableMode} placement={placement} />;
}

/** A short fixed setting: the trigger names it and its value, and each row explains itself. */
function SettingPill<Value extends string>({
  name, options, value, onChange, note, isDisabled, unavailableOption, tooltip, placement, alignment = 'start',
}: Readonly<{
  name: string;
  options: Readonly<Record<Value, SettingOption>>;
  value: Value;
  onChange: (value: Value) => void;
  note?: string;
  isDisabled: boolean;
  unavailableOption?: Value;
  /** With it Astryx marks a disabled trigger `aria-disabled` rather than `disabled`, so the tooltip stays reachable. */
  tooltip?: string;
  placement: 'above' | 'below';
  alignment?: 'start' | 'end';
}>) {
  const [open, setOpen] = useState(false);
  const hostRef = useRef<HTMLSpanElement | null>(null);
  const { onEscape, onOpenChange } = useEscapeFocus(open, setOpen, hostRef);
  const current = options[value];
  const tone = current.tone ?? 'neutral';
  const label = `${name}: ${current.label}`;
  return (
    <span ref={hostRef} className={styles.host} onKeyDownCapture={onEscape}>
      <DropdownMenu
        placement={placement}
        alignment={alignment}
        /* An `aria-disabled` trigger still passes ArrowDown to the menu, so a disabled one is held shut here. */
        isMenuOpen={open && !isDisabled}
        onOpenChange={(next) => { onOpenChange(next && !isDisabled); }}
        hasChevron={false}
        button={{
          label,
          children: <Text type="inherit" color="inherit" maxLines={1}>
            {`${name}: `}
            <span className={styles.settingValue} data-nc-tone={tone}>{current.label}</span>
          </Text>,
          'data-nc-tone': tone,
          variant: 'ghost',
          size: 'sm',
          isDisabled,
          tooltip,
          className: styles.setting,
        }}
      >
        {(Object.keys(options) as Value[]).map((option) => (
          /* A node, not a string: Astryx cuts a string description to one line, and Full access's says what it risks. */
          <Choice key={option} label={options[option].label}
            variant={options[option].tone === 'danger' ? 'destructive' : 'default'}
            description={<Text type="inherit" color="inherit">{options[option].description}</Text>}
            isSelected={value === option} isDisabled={option === unavailableOption} onSelect={() => { if (option !== value) onChange(option); }} />
        ))}
        {note !== undefined && (
          <>
            <Divider />
            <div className={styles.settingNote} role="note">
              <Text type="supporting">{note}</Text>
            </div>
          </>
        )}
      </DropdownMenu>
    </span>
  );
}

function Choice({
  label, description, isSelected, isDisabled = false, onSelect, variant = 'default',
}: Readonly<{
  label: ReactNode;
  description?: ReactNode;
  variant?: 'default' | 'destructive';
  isSelected: boolean;
  isDisabled?: boolean;
  onSelect: () => void;
}>) {
  return (
    <DropdownMenuItem
      label={label}
      variant={variant}
      className={variant === 'destructive' ? styles.destructiveChoice : undefined}
      description={description}
      onClick={onSelect}
      isDisabled={isDisabled}
      endContent={isSelected ? (
        <>
          <AstryxIcon icon="check" size="sm" color={variant === 'destructive' ? 'error' : 'accent'} />
          <VisuallyHidden>Selected</VisuallyHidden>
        </>
      ) : undefined}
    />
  );
}
