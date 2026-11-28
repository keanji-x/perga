export function basenameOf(path: string): string {
  const trimmed = path.replace(/\/+$/, '');
  return trimmed === '' ? '/' : trimmed.slice(trimmed.lastIndexOf('/') + 1);
}

export function compactPathDescription(path: string, maxLength = 42): string {
  if (path.length <= maxLength) return path;
  const parts = path.split('/').filter(Boolean);
  if (parts.length >= 2) {
    const tail = parts.slice(-2).join('/');
    if (tail.length + 2 <= maxLength) return `…/${tail}`;
  }
  return `…${path.slice(-(maxLength - 1))}`;
}

export function folderLabelParts({
  value,
  controlLabel = 'Folder',
  modeSuffix,
  showLabel = false,
}: Readonly<{
  value: string;
  showLabel?: boolean;
  controlLabel?: string;
  modeSuffix?: string;
}>): Readonly<{ visibleLabel: string; accessibleName: string }> {
  const suffix = modeSuffix ? ` · ${modeSuffix}` : '';
  if (value === '') return { visibleLabel: `Default${suffix}`, accessibleName: `Default folder${suffix}` };

  const prefix = showLabel ? `${controlLabel}: ` : '';
  const visibleLabel = `${prefix}${basenameOf(value)}${suffix}`;
  const accessibleName = showLabel ? `${visibleLabel} (${value})` : `${controlLabel}: ${value}${suffix}`;
  return { visibleLabel, accessibleName };
}
