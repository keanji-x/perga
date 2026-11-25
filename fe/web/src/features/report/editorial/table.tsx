import type { ReactNode } from 'react';
import type { BuiltinTableView } from '../../../../../core/domain/report-editorial.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import styles from './editorial.module.css';

export function EditorialTable({
  payload,
  title,
  renderText,
  renderMarkdown,
}: Readonly<{
  payload: BuiltinTableView;
  title?: string | null;
  renderText?: (text: string) => ReactNode;
  renderMarkdown?: (markdown: string, key: string) => ReactNode;
}>) {
  const text = (value: string) => renderText === undefined ? value : renderText(value);
  const label = payload.title?.trim() || title?.trim() || undefined;
  return (
    <div className={styles.tableRoot}>
      {payload.summary && (
        renderMarkdown?.(payload.summary, 'table-summary') ?? <p>{text(payload.summary)}</p>
      )}
      {payload.rows.length === 0 ? (
        <div className={styles.emptyTable} role="note">
          {label && <div>{label}</div>}
          <div>No rows to display.</div>
        </div>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table} aria-label={label}>
            <thead>
              <tr>
                {payload.columns.map((column) => (
                  <th key={column.key} scope="col" data-nc-align={column.align}>{text(column.label)}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {payload.rows.map((row, index) => (
                <tr key={index}>
                  {payload.columns.map(({ key, align }) => {
                    const cell = Object.hasOwn(row, key) ? row[key] : null;
                    return (
                      <td key={key} data-nc-align={align}>
                        {cell == null ? '—' : typeof cell === 'string' ? text(cell) : (
                          <div className={`${styles.cellContent} ${styles.tone}`} data-nc-tone={cell.tone}>
                            {cell.check && (
                              <span className={styles.cellCheck} role="img" aria-label="Passed">
                                <Icon name="check" size="sm" />
                              </span>
                            )}
                            {cell.code && <code className={styles.cellCode}>{cell.code}</code>}
                            <div className={styles.cellText}>
                              {cell.markdown && renderMarkdown !== undefined
                                ? renderMarkdown(cell.text, `table-${index}-${key}`)
                                : text(cell.text)}
                            </div>
                            {cell.tag && <span className={styles.cellTag}>{text(cell.tag)}</span>}
                          </div>
                        )}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
