// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { PlannerAttachment, UploadAttachmentResponse } from '../../../../core/api/generated/wire.ts';
import { useState } from '../../ui/state/public.ts';
import {
  ATTACHED_WORKSPACE_REASON, PlannerAttachButton, PlannerAttachmentDrawer, usePlannerAttachments,
  NO_UPLOAD, type AttachmentStore, type PlannerAttachments, type UploadAttachment, type UploadState,
} from './attachments.tsx';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function uploaded(index: number): UploadAttachmentResponse {
  const id = `att_0189bc3f2b1a4c7d9e4f1a2b3c4d5e6${index}`;
  return {
    attachmentId: id, contentType: 'image/png', size: 12,
    url: `/api/v1/sessions/card-1/attachments/${id}`,
  };
}

/** A png `File` whose bytes never matter — the server decides the format. */
function png(name = 'shot.png'): File {
  return new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], name, { type: 'image/png' });
}

let latest: PlannerAttachments | null = null;
/** Every card's images, as the caller holds them; read by the cases that switch cards. */
let held: Readonly<Record<string, readonly PlannerAttachment[]>> = {};

/** The caller's per-card composer images and uploads, as the router keeps them in the conversation registry. */
function useCardImages(cardId: string): AttachmentStore {
  const [byCard, setByCard] = useState<Readonly<Record<string, readonly PlannerAttachment[]>>>({});
  held = byCard;
  const [uploads, setUploads] = useState<Readonly<Record<string, UploadState>>>({});
  return {
    items: byCard[cardId] ?? [], update: (card, next) => setByCard((current) => ({ ...current, [card]: next(current[card] ?? []) })),
    upload: uploads[cardId] ?? NO_UPLOAD, editUpload: (card, next) => setUploads((current) => ({ ...current, [card]: next(current[card] ?? NO_UPLOAD) })),
  };
}

function Harness({ upload, supported = true, card = 'card-1' }: {
  upload: UploadAttachment; supported?: boolean; card?: string;
}) {
  const attachments = usePlannerAttachments(upload, card, useCardImages(card));
  latest = attachments;
  return (
    <>
      <PlannerAttachButton
        attachments={attachments}
        support={{ available: supported, reason: ATTACHED_WORKSPACE_REASON }}
      />
      <PlannerAttachmentDrawer attachments={attachments} />
    </>
  );
}

/** The hidden `<input type="file">`, found by its type: it deliberately has no accessible name. */
function picker(): HTMLInputElement {
  const found = document.querySelector<HTMLInputElement>('input[type="file"]');
  if (found === null) throw new Error('no file input rendered');
  return found;
}

/** The control itself. */
function attachButton(): HTMLButtonElement {
  return screen.getByRole<HTMLButtonElement>('button', { name: 'Attach an image' });
}

/** True whether the button is natively disabled or `aria-disabled` (Astryx switches to the latter when the button carries a tooltip). */
function attachBlocked(): boolean {
  const button = attachButton();
  return button.disabled || button.getAttribute('aria-disabled') === 'true';
}

async function pick(...files: File[]) {
  await act(async () => {
    fireEvent.change(picker(), { target: { files } });
    await Promise.resolve();
  });
}

describe('planner attachments', () => {
  it('opens the file picker when the control is pressed', async () => {
    render(<Harness upload={vi.fn<UploadAttachment>()} />);
    const opened = vi.fn();
    picker().addEventListener('click', opened);
    await userEvent.click(attachButton());
    expect(opened).toHaveBeenCalledTimes(1);
  });

  it('shows optimistic ObjectURL preview immediately and revokes it on remove or unmount', async () => {
    const createSpy = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:optimistic-0');
    const revokeSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const upload = vi.fn<UploadAttachment>().mockResolvedValue(() => uploaded(0));
    const mounted = render(<Harness upload={upload} />);
    await pick(png());

    expect(createSpy).toHaveBeenCalledTimes(1);
    expect(upload).toHaveBeenCalledTimes(1);
    const [readBytes, contentType, options] = upload.mock.calls[0] ?? [];
    expect(await readBytes()).toBeInstanceOf(Uint8Array);
    expect(contentType).toBe('image/png');
    expect(options?.filename).toBe('shot.png');

    const thumb = document.querySelector('img');
    expect(thumb?.getAttribute('src')).toBe('blob:optimistic-0');
    expect(latest?.ids).toEqual([uploaded(0).attachmentId]);
    expect(latest?.canSendWithoutText).toBe(true);
    expect(latest?.sendBlocked).toBe(false);

    mounted.unmount();
    expect(revokeSpy).toHaveBeenCalledWith('blob:optimistic-0');
  });

  it('removes a picked image before it is ever sent and revokes its ObjectURL', async () => {
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:remove-me');
    const revokeSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const upload = vi.fn<UploadAttachment>().mockResolvedValue(() => uploaded(0));
    render(<Harness upload={upload} />);
    await pick(png());
    expect(latest?.ids).toHaveLength(1);

    act(() => { fireEvent.click(screen.getByLabelText('Remove Image 1')); });
    expect(latest?.ids).toEqual([]);
    expect(screen.queryByLabelText('Remove Image 1')).toBeNull();
    expect(revokeSpy).toHaveBeenCalledWith('blob:remove-me');
  });

  it('keeps attach button enabled while uploading and allows removing an in-flight upload without reappearing', async () => {
    let urlCounter = 0;
    vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:flight-${urlCounter++}`);
    const revokeSpy = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const resolvers: ((value: () => UploadAttachmentResponse) => void)[] = [];
    const signals: (AbortSignal | undefined)[] = [];
    const upload = vi.fn<UploadAttachment>().mockImplementation((_readBytes, _type, opts) => {
      signals.push(opts?.signal);
      return new Promise((resolve) => { resolvers.push(resolve); });
    });
    render(<Harness upload={upload} />);

    await pick(png('first.png'));
    expect(latest?.busy).toBe(true);
    expect(latest?.sendBlocked).toBe(true);
    expect(attachBlocked()).toBe(false);

    // Second pick while first is uploading must be admitted immediately.
    await pick(png('second.png'));
    expect(latest?.draftItems).toHaveLength(2);
    expect(upload).toHaveBeenCalledTimes(2);

    // Cancel the first uploading item.
    act(() => { fireEvent.click(screen.getByLabelText('Remove Image 1')); });
    expect(signals[0]?.aborted).toBe(true);
    expect(revokeSpy).toHaveBeenCalledWith('blob:flight-0');
    expect(latest?.draftItems).toHaveLength(1);

    // Even if first promise settles late, it must not reappear.
    await act(async () => {
      resolvers[0](() => uploaded(0));
      resolvers[1](() => uploaded(1));
      await Promise.resolve();
    });
    expect(latest?.ids).toEqual([uploaded(1).attachmentId]);
    expect(latest?.sendBlocked).toBe(false);
  });

  it('refuses a file that is not one of the four formats without a round trip', async () => {
    const upload = vi.fn<UploadAttachment>().mockResolvedValue(() => uploaded(0));
    render(<Harness upload={upload} />);
    await pick(new File(['note'], 'notes.txt', { type: 'text/plain' }));

    expect(upload).not.toHaveBeenCalled();
    expect(screen.getByRole('alert').textContent).toContain('PNG, JPEG, GIF or WebP');
  });

  it('stops at eight and says so', async () => {
    let seq = 0;
    const upload = vi.fn<UploadAttachment>()
      .mockImplementation(() => Promise.resolve(() => uploaded(seq++)));
    render(<Harness upload={upload} />);
    for (let index = 0; index < 8; index += 1) await pick(png(`shot-${index}.png`));
    expect(latest?.ids).toHaveLength(8);
    expect(latest?.atCapacity).toBe(true);
    expect(attachBlocked()).toBe(true);

    upload.mockClear();
    await pick(png('ninth.png'));
    expect(upload).not.toHaveBeenCalled();
  });

  it('shows server refusal, blocks send on failed item, and retries in place', async () => {
    const upload = vi.fn<UploadAttachment>()
      .mockRejectedValueOnce(new Error('attachment budget exhausted for this card (64 MiB).'))
      .mockResolvedValueOnce(() => uploaded(0));
    render(<Harness upload={upload} />);
    await pick(png());

    expect(screen.getByRole('alert').textContent).toContain('budget exhausted');
    expect(latest?.ids).toEqual([]);
    expect(latest?.hasFailed).toBe(true);
    expect(latest?.sendBlocked).toBe(true);

    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: 'Retry Image 1' }));
      await Promise.resolve();
    });
    expect(latest?.hasFailed).toBe(false);
    expect(latest?.sendBlocked).toBe(false);
    expect(latest?.ids).toEqual([uploaded(0).attachmentId]);
  });

  it('puts an upload that finished after the card changed into the card it started for', async () => {
    let settle: ((value: () => UploadAttachmentResponse) => void) | undefined;
    const upload = vi.fn<UploadAttachment>()
      .mockImplementation(() => new Promise((resolve) => { settle = resolve; }));
    const { rerender } = render(<Harness upload={upload} card="card-1" />);
    await pick(png());
    expect(upload).toHaveBeenCalledTimes(1);
    expect(latest?.ids).toEqual([]);

    rerender(<Harness upload={upload} card="card-2" />);
    await act(async () => {
      settle?.(() => uploaded(0));
      await Promise.resolve();
    });
    expect(latest?.ids).toEqual([]);
    expect(screen.queryByLabelText('Remove Image 1')).toBeNull();
    expect(held['card-1']?.map((item) => item.id)).toEqual([uploaded(0).attachmentId]);
    rerender(<Harness upload={upload} card="card-1" />);
    expect(latest?.ids).toEqual([uploaded(0).attachmentId]);
  });

  it('does adopt an upload that finished while the same card was still open', async () => {
    let settle: ((value: () => UploadAttachmentResponse) => void) | undefined;
    const upload = vi.fn<UploadAttachment>()
      .mockImplementation(() => new Promise((resolve) => { settle = resolve; }));
    render(<Harness upload={upload} card="card-1" />);
    await pick(png());
    await act(async () => {
      settle?.(() => uploaded(0));
      await Promise.resolve();
    });
    expect(latest?.ids).toEqual([uploaded(0).attachmentId]);
  });

  it('is unavailable with a reason on a track that cannot take attachments', async () => {
    render(<Harness upload={vi.fn<UploadAttachment>()} supported={false} />);
    expect(attachBlocked()).toBe(true);
    await userEvent.hover(attachButton());
    expect(await screen.findByText(ATTACHED_WORKSPACE_REASON)).toBeTruthy();
  });
});

it('an old card upload cannot release the new card upload busy state', async () => {
  const pending: ((value: () => UploadAttachmentResponse) => void)[] = [];
  const upload: UploadAttachment = () => new Promise(resolve => pending.push(resolve));
  const mounted = render(<Harness upload={upload} card="card-1" />);
  await pick(png()); mounted.rerender(<Harness upload={upload} card="card-2" />); await pick(png());
  expect(latest?.busy).toBe(true);
  await act(async () => { pending[0](() => uploaded(0)); await Promise.resolve(); });
  expect(latest?.busy).toBe(true); expect(latest?.ids).toEqual([]);
  expect(held['card-1']?.map((item) => item.id)).toEqual([uploaded(0).attachmentId]);
  await act(async () => { pending[1](() => uploaded(1)); await Promise.resolve(); });
  expect(latest?.busy).toBe(false); expect(latest?.ids).toEqual([uploaded(1).attachmentId]);
});

it('serializes a batch and admits only eight images before React commits', async () => {
  let count = 0;
  const upload = vi.fn<UploadAttachment>().mockImplementation(async () => {
    await Promise.resolve();
    return () => uploaded(count++);
  });
  render(<Harness upload={upload} />);
  await act(async () => {
    const attach = latest!.attach;
    await Promise.all(Array.from({ length: 10 }, (_, index) => attach(png(`shot-${index}.png`))));
  });
  expect(upload).toHaveBeenCalledTimes(8);
  expect(latest?.ids).toEqual(Array.from({ length: 8 }, (_, index) => uploaded(index).attachmentId));
  expect(latest?.error).toBe('A message can carry at most 8 images.');
  expect(latest?.busy).toBe(false);
});

it('keeps every queued image and its upload in the originating card after a switch', async () => {
  const pending: ((value: () => UploadAttachmentResponse) => void)[] = [];
  const upload = vi.fn<UploadAttachment>().mockImplementation(async (readBytes) => {
    await readBytes();
    return new Promise(resolve => pending.push(resolve));
  });
  const mounted = render(<Harness upload={upload} card="card-1" />);
  let batch: Promise<void[]>;
  await act(async () => {
    const attach = latest!.attach;
    batch = Promise.all([attach(png('first.png')), attach(png('second.png'))]);
    await Promise.resolve();
  });
  expect(upload).toHaveBeenCalledTimes(2);
  expect(pending).toHaveLength(1);
  mounted.rerender(<Harness upload={upload} card="card-2" />);
  await act(async () => { pending[0](() => uploaded(0)); await Promise.resolve(); });
  expect(upload).toHaveBeenCalledTimes(2);
  expect(latest?.busy).toBe(false);
  expect(latest?.ids).toEqual([]);
  await act(async () => { pending[1](() => uploaded(1)); await batch; });
  expect(held['card-1']?.map(item => item.id)).toEqual([uploaded(0).attachmentId, uploaded(1).attachmentId]);
  mounted.rerender(<Harness upload={upload} card="card-1" />);
  expect(latest?.busy).toBe(false);
  expect(latest?.ids).toEqual([uploaded(0).attachmentId, uploaded(1).attachmentId]);
});

it('continues a queued batch after an upload refusal', async () => {
  const upload = vi.fn<UploadAttachment>().mockRejectedValueOnce(new Error('budget refusal'))
    .mockResolvedValueOnce(() => uploaded(1));
  render(<Harness upload={upload} />);
  await act(async () => {
    const attach = latest!.attach;
    await Promise.all([attach(png('first.png')), attach(png('second.png'))]);
  });
  expect(upload).toHaveBeenCalledTimes(2);
  expect(latest?.ids).toEqual([uploaded(1).attachmentId]);
  expect(latest?.error).toBe('budget refusal');
  expect(latest?.busy).toBe(false);
});

it('treats unmount abort as cancellation without writing a stale refusal banner', async () => {
  const uploadStates: Record<string, UploadState> = {};
  const upload = vi.fn<UploadAttachment>((_readBytes, _contentType, options) => new Promise((_resolve, reject) => {
    options?.signal?.addEventListener('abort', () => {
      reject(new DOMException('Aborted', 'AbortError'));
    }, { once: true });
  }));

  function UnmountHarness() {
    const store: AttachmentStore = {
      items: [],
      update: () => undefined,
      upload: uploadStates['card-unmount'] ?? NO_UPLOAD,
      editUpload: (id, next) => {
        uploadStates[id] = next(uploadStates[id] ?? NO_UPLOAD);
      },
    };
    latest = usePlannerAttachments(upload, 'card-unmount', store);
    return null;
  }

  const mounted = render(<UnmountHarness />);
  act(() => {
    void latest!.attach(png('in-flight.png'));
  });
  mounted.unmount();
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
  expect(uploadStates['card-unmount']?.refusal ?? null).toBeNull();
  expect(uploadStates['card-unmount']?.inFlight ?? 0).toBe(0);
});
