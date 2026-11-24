import { act, cleanup, fireEvent, render, renderHook, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { useRef } from 'react';
import { MAX_ATTACHMENT_BYTES, MAX_ATTACHMENTS_PER_MESSAGE } from '../../../../core/api/perga-v1.ts';
import { TOO_MANY_IMAGES, TOO_MANY_IMAGE_BYTES } from '../../../../core/domain/conversation-commands.ts';
import { useState } from '../../ui/state/public.ts';
import { PlannerAttachButton, PlannerAttachmentDrawer, usePlannerAttachments, type AttachmentDraft } from './attachments.tsx';

function image(type = 'image/png', size = 4) {
  return new File([new Uint8Array(size)], 'screenshot.png', { type });
}

function useDraft() {
  const current = useRef<AttachmentDraft>({});
  const [draft, setDraft] = useState<AttachmentDraft>(current.current);
  return usePlannerAttachments(draft, change => {
    current.current = change(current.current);
    setDraft(current.current);
  });
}

const createPreview = vi.fn<(file: File) => string>();

beforeEach(() => {
  let next = 0;
  createPreview.mockReset().mockImplementation(() => `blob:preview-${++next}`);
  vi.stubGlobal('URL', class extends URL {
    static createObjectURL = createPreview;
  });
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

it('opens the picker and previews the original selected file synchronously', () => {
  function Picker() {
    const attachments = useDraft();
    return <><PlannerAttachButton attachments={attachments} available /><PlannerAttachmentDrawer attachments={attachments} /></>;
  }
  const { container } = render(<Picker />);
  const picker = container.querySelector('input[type="file"]')!;
  const opened = vi.fn();
  picker.addEventListener('click', opened);
  fireEvent.click(screen.getByRole('button', { name: 'Attach an image' }));
  expect(opened).toHaveBeenCalledOnce();

  const file = image();
  fireEvent.change(picker, { target: { files: [file] } });
  expect(createPreview).toHaveBeenCalledExactlyOnceWith(file);
  expect(container.querySelector('img')?.getAttribute('src')).toBe('blob:preview-1');
  fireEvent.click(screen.getByRole('button', { name: /Remove/ }));
  expect(container.querySelector('img')).toBeNull();
});

it.each([
  ['image/svg+xml', 4, 'Choose a PNG, JPEG, GIF or WebP image.'],
  ['image/png', MAX_ATTACHMENT_BYTES + 1, 'Each image must be at most 10 MiB.'],
  ['image/png', 0, 'Choose an image that is not empty.'],
] as const)('rejects invalid type or size (%s, %d) before creating a preview', (type, size, reason) => {
  const { result } = renderHook(useDraft);
  act(() => result.current.attach([image(type, size)]));
  expect(result.current.items).toEqual([]);
  expect(result.current.error).toBe(reason);
  expect(createPreview).not.toHaveBeenCalled();
});

it('accepts the size limit and enforces the count across same-tick selections', () => {
  const { result } = renderHook(useDraft);
  const first = image('image/png', MAX_ATTACHMENT_BYTES);
  const more = Array.from({ length: MAX_ATTACHMENTS_PER_MESSAGE }, (_, index) =>
    image(['image/jpeg', 'image/gif', 'image/webp'][index % 3]));
  act(() => {
    result.current.attach([first]);
    result.current.attach(more);
  });
  expect(result.current.items.map(item => item.file)).toEqual([first, ...more.slice(0, -1)]);
  expect(result.current.atCapacity).toBe(true);
  expect(result.current.error).toBe(TOO_MANY_IMAGES);
  expect(createPreview).toHaveBeenCalledTimes(MAX_ATTACHMENTS_PER_MESSAGE);
});

it('enforces the combined image size before allocating the preview', () => {
  const { result } = renderHook(useDraft);
  const first = image('image/png', MAX_ATTACHMENT_BYTES);
  act(() => result.current.attach([first, first, first]));
  expect(result.current.items).toHaveLength(2);
  expect(result.current.error).toBe(TOO_MANY_IMAGE_BYTES);
  expect(createPreview).toHaveBeenCalledTimes(2);
});
