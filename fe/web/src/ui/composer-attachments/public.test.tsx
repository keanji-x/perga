// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ComposerAttachmentStrip, ComposerInputField } from './public.tsx';

afterEach(cleanup);

describe('ComposerAttachmentStrip', () => {
  it('renders nothing when there are neither attachments nor an error', () => {
    const { container } = render(<ComposerAttachmentStrip items={[]} error={null} />);
    expect(container.firstChild).toBeNull();
  });

  it('renders error-only banners inside the attachment strip wrapper', () => {
    const { container } = render(
      <ComposerAttachmentStrip items={[]} error="Choose a PNG, JPEG, GIF or WebP image." />,
    );
    expect(container.querySelector('[data-nc-attachment-strip]')).not.toBeNull();
    expect(screen.getByText('That image was not attached')).toBeTruthy();
    expect(screen.getByText('Choose a PNG, JPEG, GIF or WebP image.')).toBeTruthy();
  });

  it('wires interactive open and remove controls on inline thumbnails', () => {
    const onRemove = vi.fn();
    render(
      <ComposerAttachmentStrip
        items={[{ id: 'a1', url: 'blob:a1', label: 'Screenshot' }]}
        onRemove={onRemove}
      />,
    );
    expect(screen.getByRole('button', { name: 'Open Screenshot' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Remove Screenshot' }));
    expect(onRemove).toHaveBeenCalledExactlyOnceWith('a1');
  });
});

describe('ComposerInputField', () => {
  it('ignores mixed-content paste outside the contenteditable region and forwards files inside it', () => {
    const onFiles = vi.fn();
    const clip = new File([new Uint8Array([1, 2])], 'shot.png', { type: 'image/png' });
    render(
      <ComposerInputField
        label="Draft"
        placeholder="Write..."
        onFiles={onFiles}
        inlineAttachments={<button type="button">Thumb action</button>}
      />,
    );

    fireEvent.paste(screen.getByRole('button', { name: 'Thumb action' }), {
      clipboardData: { files: [clip], items: [], getData: () => 'hello' },
    });
    expect(onFiles).not.toHaveBeenCalled();

    fireEvent.paste(screen.getByRole('textbox', { name: 'Draft' }), {
      clipboardData: { files: [clip], items: [], getData: () => 'hello' },
    });
    expect(onFiles).toHaveBeenCalledExactlyOnceWith([clip]);
  });

  it('suppresses image paste when disabled', () => {
    const onFiles = vi.fn();
    const clip = new File([new Uint8Array([1, 2])], 'shot.png', { type: 'image/png' });
    render(
      <ComposerInputField
        label="Draft"
        placeholder="Write..."
        disabled
        onFiles={onFiles}
      />,
    );
    fireEvent.paste(screen.getByRole('textbox', { name: 'Draft' }), {
      clipboardData: { files: [clip], items: [], getData: () => '' },
    });
    expect(onFiles).not.toHaveBeenCalled();
  });
});
