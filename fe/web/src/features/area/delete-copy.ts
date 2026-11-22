/** Four fields, not three: consequence and prompt have different typography, so one `description` slot cannot carry both. */
export function deleteAreaCopy(areaName: string, trackCount: number | undefined) {
  return Object.freeze({
    title: `Delete ${areaName}?`,
    consequence: trackCount === undefined
      ? 'The number of tracks is not available. Every track in this area will be deleted. This cannot be undone.'
      : trackCount === 0
      ? 'This deletes the area. This cannot be undone.'
      : trackCount === 1
      ? 'This deletes 1 track. This cannot be undone.'
      : `This deletes ${trackCount} tracks. This cannot be undone.`,
    prompt: `Type ${areaName} to confirm.`,
    confirmLabel: 'Delete area',
  });
}
