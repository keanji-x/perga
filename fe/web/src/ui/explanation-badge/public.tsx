import { Popover } from '@astryxdesign/core/Popover';
import { Tooltip } from '@astryxdesign/core/Tooltip';
import type { ReactNode } from 'react';
import { Icon, type IconName } from '../icon/public.tsx';
import styles from './explanation-badge.module.css';

/** A quiet icon-only editorial indicator with tooltip and click/keyboard/touch explanation; Popover owns dismissal and motion. */
export function ExplanationBadge({ label, icon = 'folder-open', children }: Readonly<{ label: string; icon?: IconName; children: ReactNode }>) {
  return <Popover label={label} content={<div className={styles.explanation}>{children}</div>}>
    {(trigger) => <Tooltip content={label} placement="below">
      <button {...trigger} type="button" className={styles.trigger} aria-label={label}>
        <span className={styles.icon} aria-hidden="true"><Icon name={icon} size="sm" /></span>
        <span className={styles.srOnly}>{label}</span>
      </button>
    </Tooltip>}
  </Popover>;
}
