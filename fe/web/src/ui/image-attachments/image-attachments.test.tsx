// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  AttachmentGallery,
  DraftAttachmentStrip,
  extractClipboardPayload,
  handleComposerPasteCapture,
  ImageAttachButton,
  useDropzoneHighlight,
} from './public.tsx';

afterEach(cleanup);

function makeFile(name: string, type: string, content = 'img'): File {
  return new File([content], name, { type });
}

function makeDataTransfer(options: {
  items?: readonly { kind: 'file' | 'string'; file?: File | null }[];
  files?: readonly File[];
  text?: string;
}): DataTransfer {
  const items = (options.items ?? []).map((entry) => ({
    kind: entry.kind,
    type: entry.file?.type ?? 'text/plain',
    getAsFile: () => entry.file ?? null,
  }));
  return {
    items: items as unknown as DataTransferItemList,
    files: (options.files ?? []) as unknown as FileList,
    types: [...(options.text ? ['text/plain'] : []), ...((options.files?.length || items.length) ? ['Files'] : [])],
    getData: (mime: string) => (mime === 'text/plain' ? (options.text ?? '') : ''),
  } as unknown as DataTransfer;
}

describe('clipboard payload extraction and paste capture', () => {
  it('deduplicates Chromium clipboard items and files while preserving multiple distinct images', () => {
    const img1 = makeFile('shot1.png', 'image/png', 'aaa');
    const img2 = makeFile('shot2.jpg', 'image/jpeg', 'bbbb');
    const dt = makeDataTransfer({
      items: [{ kind: 'file', file: img1 }, { kind: 'file', file: img2 }],
      files: [img1, img2],
      text: 'caption',
    });

    const payload = extractClipboardPayload(dt);
    expect(payload.images).toEqual([img1, img2]);
    expect(payload.otherFiles).toEqual([]);
    expect(payload.plainText).toBe('caption');
  });

  it('intercepts pure image paste without inserting text', () => {
    const img = makeFile('shot.png', 'image/png');
    const onImages = vi.fn();
    const insertText = vi.fn();
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    const handled = handleComposerPasteCapture({
      clipboardData: makeDataTransfer({ items: [{ kind: 'file', file: img }], files: [img] }),
      preventDefault,
      stopPropagation,
    } as unknown as React.ClipboardEvent<HTMLElement>, { onImages, insertText });

    expect(handled).toBe(true);
    expect(preventDefault).toHaveBeenCalledOnce();
    expect(stopPropagation).toHaveBeenCalledOnce();
    expect(onImages).toHaveBeenCalledWith([img]);
    expect(insertText).not.toHaveBeenCalled();
  });

  it('handles mixed text and image paste without dropping plain text', () => {
    const img = makeFile('shot.png', 'image/png');
    const onImages = vi.fn();
    const insertText = vi.fn();
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    const handled = handleComposerPasteCapture({
      clipboardData: makeDataTransfer({ items: [{ kind: 'file', file: img }], text: 'hello mixed' }),
      preventDefault,
      stopPropagation,
    } as unknown as React.ClipboardEvent<HTMLElement>, { onImages, insertText });

    expect(handled).toBe(true);
    expect(preventDefault).toHaveBeenCalledOnce();
    expect(stopPropagation).toHaveBeenCalledOnce();
    expect(onImages).toHaveBeenCalledWith([img]);
    expect(insertText).toHaveBeenCalledWith('hello mixed');
  });

  it('leaves pure text paste untouched for native handling', () => {
    const onImages = vi.fn();
    const insertText = vi.fn();
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    const handled = handleComposerPasteCapture({
      clipboardData: makeDataTransfer({ text: 'plain text only' }),
      preventDefault,
      stopPropagation,
    } as unknown as React.ClipboardEvent<HTMLElement>, { onImages, insertText });

    expect(handled).toBe(false);
    expect(preventDefault).not.toHaveBeenCalled();
    expect(stopPropagation).not.toHaveBeenCalled();
    expect(onImages).not.toHaveBeenCalled();
    expect(insertText).not.toHaveBeenCalled();
  });

  it('intercepts non-image files without text so the caller can show a refusal', () => {
    const pdf = makeFile('spec.pdf', 'application/pdf');
    const onUnsupportedFiles = vi.fn();
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    const handled = handleComposerPasteCapture({
      clipboardData: makeDataTransfer({ files: [pdf] }),
      preventDefault,
      stopPropagation,
    } as unknown as React.ClipboardEvent<HTMLElement>, { onUnsupportedFiles });

    expect(handled).toBe(true);
    expect(preventDefault).toHaveBeenCalledOnce();
    expect(stopPropagation).toHaveBeenCalledOnce();
    expect(onUnsupportedFiles).toHaveBeenCalledWith([pdf]);
  });

  it('preserves accompanying plain text when attachments are disabled on an editable composer', () => {
    const img = makeFile('shot.png', 'image/png');
    const onImages = vi.fn();
    const insertText = vi.fn();
    const preventDefault = vi.fn();
    const stopPropagation = vi.fn();

    const handled = handleComposerPasteCapture({
      clipboardData: makeDataTransfer({ items: [{ kind: 'file', file: img }], text: 'keep this text' }),
      preventDefault,
      stopPropagation,
    } as unknown as React.ClipboardEvent<HTMLElement>, {
      disabled: true,
      inputDisabled: false,
      onImages,
      insertText,
    });

    expect(handled).toBe(true);
    expect(preventDefault).toHaveBeenCalledOnce();
    expect(stopPropagation).toHaveBeenCalledOnce();
    expect(onImages).not.toHaveBeenCalled();
    expect(insertText).toHaveBeenCalledWith('keep this text');
  });
});

describe('useDropzoneHighlight', () => {
  it('clears highlight after dragging across nested children and leaving the container', () => {
    const onDropFiles = vi.fn();
    function DropHarness() {
      const { isDragOver, dropzoneProps } = useDropzoneHighlight({ enabled: true, onDropFiles });
      return (
        <div>
          <div data-testid="zone" data-active={isDragOver ? 'yes' : 'no'} {...dropzoneProps}>
            <span data-testid="child-a">A</span>
            <span data-testid="child-b">B</span>
          </div>
          <div data-testid="outside">Outside</div>
        </div>
      );
    }

    render(<DropHarness />);
    const zone = screen.getByTestId('zone');
    const childA = screen.getByTestId('child-a');
    const childB = screen.getByTestId('child-b');
    const outside = screen.getByTestId('outside');
    const dt = makeDataTransfer({ files: [makeFile('drop.png', 'image/png')] });

    fireEvent.dragEnter(zone, { dataTransfer: dt });
    fireEvent.dragEnter(childA, { dataTransfer: dt });
    fireEvent.dragLeave(childA, { dataTransfer: dt, relatedTarget: childB });
    fireEvent.dragEnter(childB, { dataTransfer: dt });
    expect(zone.getAttribute('data-active')).toBe('yes');

    // Leaving the container to an outside target must clear highlight immediately (no stuck depth counter).
    fireEvent.dragLeave(childB, { dataTransfer: dt, relatedTarget: outside });
    expect(zone.getAttribute('data-active')).toBe('no');
  });
});

describe('ImageAttachButton, DraftAttachmentStrip and AttachmentGallery', () => {
  it('supports multi-file selection and resets input value after change', () => {
    const onPickFiles = vi.fn();
    render(<ImageAttachButton onPickFiles={onPickFiles} />);
    const input = document.querySelector<HTMLInputElement>('input[type="file"]')!;
    expect(input.multiple).toBe(true);
    const f1 = makeFile('1.png', 'image/png');
    const f2 = makeFile('2.webp', 'image/webp');
    fireEvent.change(input, { target: { files: [f1, f2] } });
    expect(onPickFiles).toHaveBeenCalledWith([f1, f2]);
  });

  it('renders retry action on failed items and isolates remove click from lightbox', () => {
    const onRemove = vi.fn();
    const onRetry = vi.fn();
    render(
      <DraftAttachmentStrip
        items={[
          { id: 'loc-1', src: 'blob:one', label: 'Image 1', status: 'ready' },
          { id: 'loc-2', src: 'blob:two', label: 'Image 2', status: 'failed', error: 'Upload failed' },
        ]}
        onRemove={onRemove}
        onRetry={onRetry}
        statusMessage="部分图片上传失败，请重试或移除后再发送。"
        error="Upload failed"
      />,
    );

    expect(document.querySelector('[data-nc-attachment-status]')?.textContent).toContain('部分图片上传失败');
    fireEvent.click(screen.getByRole('button', { name: 'Retry Image 2' }));
    expect(onRetry).toHaveBeenCalledWith('loc-2');

    // Clicking Remove must NOT open the Lightbox dialog.
    fireEvent.click(screen.getByLabelText('Remove Image 1'));
    expect(onRemove).toHaveBeenCalledWith('loc-1');
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('renders AttachmentGallery buttons for each image', () => {
    render(
      <AttachmentGallery
        images={[
          { id: 'att_1', src: '/api/v1/sessions/s1/attachments/att_1' },
          { id: 'att_2', src: '/api/v1/sessions/s1/attachments/att_2' },
        ]}
      />,
    );
    const buttons = screen.getAllByRole('button');
    expect(buttons).toHaveLength(2);
    expect(buttons[0].getAttribute('aria-haspopup')).toBe('dialog');
  });

  it('hides Remove and Retry controls when DraftAttachmentStrip is disabled during submission', () => {
    render(
      <DraftAttachmentStrip
        disabled
        items={[
          { id: 'loc-1', src: 'blob:one', label: 'Image 1', status: 'ready' },
          { id: 'loc-2', src: 'blob:two', label: 'Image 2', status: 'failed', error: 'Upload failed' },
        ]}
        onRemove={vi.fn()}
        onRetry={vi.fn()}
      />,
    );
    expect(screen.queryByLabelText('Remove Image 1')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Retry Image 2' })).toBeNull();
  });
});
