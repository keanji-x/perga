/** The destructive-confirm copy, declared once so every delete surface reads the same sentence. */
export const DELETE_TRACK_COPY = Object.freeze({
  title: 'Delete this track?',
  description: 'The track, its cards, and their terminals are removed. This cannot be undone.',
  confirmLabel: 'Delete track',
});

export const DELETE_CARD_COPY = Object.freeze({
  title: 'Delete this card?',
  description: 'The card and anything running in it — its terminal or agent session — are removed. This cannot be undone.',
  confirmLabel: 'Delete card',
});
