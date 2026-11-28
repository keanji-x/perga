const RETURN_FOCUS_ATTRIBUTE = 'data-nc-return-focus';

export type ReturnFocusKey =
  | 'focus-workspace'
  | `focus-area:${string}`
  | `workspace-area:${string}`
  | 'report-area'
  | 'report-title';

export function returnFocusProps(key: ReturnFocusKey) {
  return { [RETURN_FOCUS_ATTRIBUTE]: key };
}

export function findReturnFocusTarget(key: ReturnFocusKey): HTMLButtonElement | null {
  for (const button of document.querySelectorAll('button')) {
    if (button.getAttribute(RETURN_FOCUS_ATTRIBUTE) === key) return button;
  }
  return null;
}
