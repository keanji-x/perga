// The `src`/`path` rules of the report blocks that load a URL on this origin: `app` and `preview`
// take any same-origin path (the renderer re-checks the origin). A payload the kernel accepts is
// exactly one this renders.

import { z } from 'zod';
import { max2048CodePoints } from './report-table.js';

/**
 * A same-origin absolute path: a leading `/`, not `//`, and no backslashes (browsers normalize `\`
 * to `/` inside a URL). The `app` renderer re-asserts the origin: two checks for the one block that
 * loads someone else's markup.
 */
export function sameOriginPath() {
  return max2048CodePoints(z.string()
  .regex(/^\/(?!\/)[^\\]*$/, { message: 'must be a same-origin absolute path' })
  .refine((value) => {
    for (let index = 0; index < value.length; index += 1) {
      const code = value.charCodeAt(index);
      if (code < 0x20 || (code >= 0x7f && code <= 0x9f)) return false;
    }
    return true;
  }, { message: 'must not contain control characters' }));
}
