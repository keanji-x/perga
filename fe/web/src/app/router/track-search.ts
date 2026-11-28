export type MobilePanel = 'outline' | 'cards' | 'tasks' | 'conversations';

/** The whitelisted track query string, as the router validates and rebuilds it. */
export type TrackSearch = Readonly<{
  card?: string;
  file?: string;
  line?: number | string;
  panel?: MobilePanel;
}>;
