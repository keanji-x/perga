import '../../styles/entry.css';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { commands, page, userEvent } from 'vitest/browser';
import type { PlannerAttachment, UploadAttachmentResponse } from '../../../../core/api/generated/wire.ts';
import { ChatComposer } from '../../features/chat/thread/public.tsx';
import { MessageEntry } from '../../features/chat/thread/message-entry.tsx';
import {
  IMAGE_UNSUPPORTED_REASON,
  NO_UPLOAD,
  PlannerAttachButton,
  PlannerAttachmentDrawer,
  usePlannerAttachments,
  type AttachmentStore,
  type UploadAttachment,
  type UploadState,
} from '../../features/planner/attachments.tsx';
import { useState } from '../../ui/state/public.ts';
import { ConversationSurface } from './mobile-conversation-surface.tsx';

declare module 'vitest/browser' {
  interface BrowserCommands {
    tap(selector: string): Promise<void>;
  }
}

const VALID_PNG_BYTES = new Uint8Array([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
  0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
  0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
  0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
  0xde, 0x00, 0x00, 0x00, 0x0c, 0x49, 0x44, 0x41,
  0x54, 0x08, 0xd7, 0x63, 0xf8, 0xcf, 0xc0, 0x00,
  0x00, 0x03, 0x01, 0x01, 0x00, 0x18, 0xdd, 0x8d,
  0xb0, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e,
  0x44, 0xae, 0x42, 0x60, 0x82,
]);
const VALID_PNG_DATA_URL = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mP4z8AAAAMBAQAY3Y2wAAAAAElFTkSuQmCC';

afterEach(async () => {
  cleanup();
  await page.viewport(1024, 1366);
});

it('supports coarse-pointer touch paste, Lightbox tap, and isolated remove tap in mobile composer and thread', async () => {
  await page.viewport(390, 844);
  let seq = 0;
  const upload = vi.fn<UploadAttachment>(async (readBytes) => {
    await readBytes();
    const id = `att_touch_${++seq}`;
    const response: UploadAttachmentResponse = {
      attachmentId: id,
      contentType: 'image/png',
      size: VALID_PNG_BYTES.byteLength,
      url: VALID_PNG_DATA_URL,
    };
    return () => response;
  });

  function MobileAttachmentHarness() {
    const [items, setItems] = useState<readonly PlannerAttachment[]>([]);
    const [uploadState, setUploadState] = useState<UploadState>(NO_UPLOAD);
    const [sent, setSent] = useState<readonly PlannerAttachment[]>([]);
    const store: AttachmentStore = {
      items,
      update: (_id, next) => setItems((current) => next(current)),
      upload: uploadState,
      editUpload: (_id, next) => setUploadState((current) => next(current)),
    };
    const attachments = usePlannerAttachments(upload, 'mobile-card', store);
    return (
      <ConversationSurface
        mobileSheet
        open
        title="Mobile Attachments"
        onClose={() => undefined}
        footer={(
          <ChatComposer
            layout="mobile"
            onSend={() => {
              setSent(attachments.items);
              attachments.clear();
            }}
            allowEmptyText={attachments.canSendWithoutText}
            sendDisabled={attachments.sendBlocked}
            onFiles={(files) => { void attachments.attachMany(files); }}
            drawer={<PlannerAttachmentDrawer attachments={attachments} />}
            footerActions={(
              <PlannerAttachButton
                attachments={attachments}
                support={{ available: true, reason: IMAGE_UNSUPPORTED_REASON }}
              />
            )}
          />
        )}
      >
        {sent.length > 0 && (
          <MessageEntry
            id="msg-1"
            entryKey="msg-1"
            mobile
            atMs={Date.now()}
            timestampFormatter={null}
            author="you"
            human={undefined}
            imported={false}
            text=""
            attachments={sent}
            opens
            gapLabel={null}
            queued={false}
            edited={false}
            replacement={false}
            imageFiles={null}
          />
        )}
      </ConversationSurface>
    );
  }

  render(<MobileAttachmentHarness />);
  await expect.element(page.getByRole('dialog', { name: 'Mobile Attachments', exact: true })).toBeVisible();

  const field = await screen.findByRole('textbox', { name: 'Message' });
  const dt = new DataTransfer();
  dt.items.add(new File([VALID_PNG_BYTES], 'a.png', { type: 'image/png' }));
  dt.items.add(new File([VALID_PNG_BYTES], 'b.png', { type: 'image/png' }));
  act(() => {
    field.dispatchEvent(new ClipboardEvent('paste', { bubbles: true, cancelable: true, clipboardData: dt }));
  });

  await waitFor(() => {
    expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(2);
  });

  // Tap remove on Image 2 -> removes without opening Lightbox.
  await commands.tap('[aria-label="Remove Image 2"]');
  await waitFor(() => {
    expect(document.querySelectorAll('[data-nc-attachment-item="ready"]')).toHaveLength(1);
  });
  expect(document.querySelectorAll('dialog[open]')).toHaveLength(1); // Only the mobile Sheet dialog.

  // Tap remaining composer thumbnail -> opens Lightbox dialog on top of sheet.
  await commands.tap('[data-nc-attachments] button:not([aria-label^="Remove"])');
  await waitFor(() => {
    expect(document.querySelectorAll('dialog[open]')).toHaveLength(2);
  });
  await userEvent.keyboard('{Escape}');
  await waitFor(() => {
    expect(document.querySelectorAll('dialog[open]')).toHaveLength(1);
  });

  // Send image-only and tap sent message thumbnail -> opens Lightbox.
  await commands.tap('[data-nc-send-attachment]');
  await waitFor(() => {
    expect(document.querySelector('[data-nc-turn-attachments] button')).not.toBeNull();
  });
  await commands.tap('[data-nc-turn-attachments] button');
  await waitFor(() => {
    expect(document.querySelectorAll('dialog[open]')).toHaveLength(2);
  });
});
