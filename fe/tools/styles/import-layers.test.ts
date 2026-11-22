import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import postcss from 'postcss';
import { expect, it } from 'vitest';

function layers(file: string, outer = ''): string[] {
  const result: string[] = [];
  const css = postcss.parse(readFileSync(resolve(import.meta.dirname, '../../web/src/styles', file), 'utf8'));
  css.walkAtRules('import', rule => {
    const target = /^"([^"]+)"/.exec(rule.params)?.[1] ?? '';
    const layer = /layer\(([^)]+)\)/.exec(rule.params)?.[1] ?? '';
    const effective = [outer, layer].filter(Boolean).join('.');
    if (target === './vendor.css') result.push(...layers('vendor.css', effective));
    else if (target === './astryx-theme.css') {
      const bridge = postcss.parse(readFileSync(resolve(import.meta.dirname, '../../web/src/styles/astryx-theme.css'), 'utf8'));
      const declared = new Set<string>();
      bridge.walkAtRules('layer', rule => { declared.add([effective, rule.params].filter(Boolean).join('.')); });
      result.push(...declared);
    }
    else if (!target.startsWith('.')) result.push(effective);
  });
  return result;
}
it('places vendor libraries and the Astryx bridge in their declared top-level layers', () => {
  expect(layers('entry.css')).toEqual(['astryx', 'vendor', 'vendor', 'astryx']);
});
