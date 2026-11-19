import type { ReactNode } from 'react';
import { NeigeMotion } from '../brand/motion.tsx';
import styles from './centered-composer.module.css';

/** Editorial prompt aligned to a centered writing surface. */
export function CenteredComposerIntroduction({ title, children }: Readonly<{ title: string; children?: ReactNode }>) {
  return <>
    <div className={styles.masthead}><h1 className={styles.greeting}>
      <span className={styles.mark} aria-hidden="true"><NeigeMotion kind="creation" /></span>{title}
    </h1></div>
    {children !== undefined && <p className={styles.introduction}>{children}</p>}
  </>;
}
