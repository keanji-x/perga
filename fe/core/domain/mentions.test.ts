import { describe,expect,it } from 'vitest';

import {
extractTemplateMentions,
mentionQueryOf,
selectedReadyTemplateId,
sentMentionParts,
templateMentionSuggestions
} from './mentions.js';

describe('mentionQueryOf', () => {
  it.each([
    ['#dep', { kind: 'tag', text: 'dep' }],
    ['/dep', { kind: 'track', text: 'dep' }],
    ['>roll', { kind: 'block', text: 'roll' }],
    ['+dev', { kind: 'plugin', text: 'dev' }],
  ] as const)('reads %s as one group and strips the prefix from the search', (typed, query) => {
    expect(mentionQueryOf(typed)).toEqual(query);
  });

  it.each([
    ['＃dep', { kind: 'tag', text: 'dep' }],
    ['、dep', { kind: 'track', text: 'dep' }],
    ['》roll', { kind: 'block', text: 'roll' }],
    ['＋dev', { kind: 'plugin', text: 'dev' }],
    ['＃', { kind: 'tag', text: '' }],
    ['、', { kind: 'track', text: '' }],
    ['》', { kind: 'block', text: '' }],
  ] as const)('reads %s, a Chinese IME\'s punctuation for the prefix, the same way', (typed, query) => {
    expect(mentionQueryOf(typed)).toEqual(query);
  });

  it('reads a prefix alone as that group\'s recommendations', () => {
    expect(mentionQueryOf('#')).toEqual({ kind: 'tag', text: '' });
    expect(mentionQueryOf('/')).toEqual({ kind: 'track', text: '' });
    expect(mentionQueryOf('>')).toEqual({ kind: 'block', text: '' });
    expect(mentionQueryOf('+')).toEqual({ kind: 'plugin', text: '' });
  });

  it('keeps everything typed, in all three groups, without a prefix; only the first character is one', () => {
    expect(mentionQueryOf('')).toEqual({ kind: null, text: '' });
    expect(mentionQueryOf('dep')).toEqual({ kind: null, text: 'dep' });
    expect(mentionQueryOf('a/b#c>d')).toEqual({ kind: null, text: 'a/b#c>d' });
    expect(mentionQueryOf('//x')).toEqual({ kind: 'track', text: '/x' });
  });
});

describe('sentMentionParts', () => {
  it('recovers tags, reports and block addresses from persisted text', () => {
    expect(sentMentionParts('a @`tag:部署` @`area/reports/Deploy notes.md#b_1a2b`!')).toEqual([
      { text: 'a ', label: null }, { text: '@`tag:部署`', label: '#部署' },
      { text: ' ', label: null },
      { text: '@`area/reports/Deploy notes.md#b_1a2b`', label: 'Deploy notes › b_1a2b' },
      { text: '!', label: null },
    ]);
  });

  it('keeps repeated picks and server fences containing backticks and padding', () => {
    expect(sentMentionParts('@``tag:a`b`` @`` tag:end` `` @`tag:x`@`tag:x`'))
      .toEqual([
        { text: '@``tag:a`b``', label: '#a`b' }, { text: ' ', label: null },
        { text: '@`` tag:end` ``', label: '#end`' }, { text: ' ', label: null },
        { text: '@`tag:x`', label: '#x' }, { text: '@`tag:x`', label: '#x' },
      ]);
  });

  it('displays escaped report names and keeps disambiguating suffixes', () => {
    expect(sentMentionParts('@`area/reports/a%2Fb%25%7E~12345678.md`')).toEqual([
      { text: '@`area/reports/a%2Fb%25%7E~12345678.md`', label: 'a/b%~~12345678' },
    ]);
    expect(sentMentionParts('@`unknown` then @`tag:x`')).toEqual([
      { text: '@`unknown` then ', label: null }, { text: '@`tag:x`', label: '#x' },
    ]);
  });

  it('recovers valid picks after incomplete or invalid mention prefixes', () => {
    for (const prefix of ['literal @``unfinished then ', 'literal @`unfinished then ', 'literal @`tag:unfinished then ']) {
      expect(sentMentionParts(prefix + '@`tag:x`')).toEqual([
        { text: prefix, label: null }, { text: '@`tag:x`', label: '#x' },
      ]);
      expect(sentMentionParts(prefix + '@`area/reports/Deploy notes.md`')).toEqual([
        { text: prefix, label: null },
        { text: '@`area/reports/Deploy notes.md`', label: 'Deploy notes' },
      ]);
    }
  });

  it('leaves ordinary, unknown and incomplete references unchanged', () => {
    for (const text of ['', '@bob **literal**', '@`/tmp/a`', '@`tag:`', '@`tag:a,b`', '@`tag:a b`', '@`tag:a\u0000b`',
      '@`area/reports/bad%.md`', '@`area/reports/.md`', '@`area/reports/a.md#`', '@``tag:x`']) {
      expect(sentMentionParts(text)).toEqual(text === '' ? [] : [{ text, label: null }]);
    }
  });
});

it('displays canonical server inserts without resolving identity or changing bytes', () => {
  const report = '@``/研发%2F100%25%7E/%2E notes `x`#~12345678/report.md#b_1``';
  expect(sentMentionParts(report)).toEqual([{ text: report, label: '. notes `x`#~12345678 › b_1' }]);
  const empty = '@`/~area1234/~track123/report.md`';
  expect(sentMentionParts(empty)).toEqual([{ text: empty, label: '~track123' }]);
});

it.each([':plan', '：plan'])('recognizes template prefix %s', typed => {
  expect(mentionQueryOf(typed)).toEqual({ kind: 'template', text: 'plan' });
});

it('finds template titles, IDs and descriptions with or without the prefix', () => {
  const templates = [{ id: 'small-change', title: 'Small change', description: 'Review a patch',
    availability: { status: 'ready' as const }, tasks: [] }];
  for (const query of ['small', ':change', '：SMALL', 'patch']) {
    expect(templateMentionSuggestions(templates, query)).toEqual([{
      id: 'template:small-change', kind: 'template', label: 'Small change', chip: 'Small change',
      detail: 'Review a patch', insert: '@`template:small-change`',
    }]);
  }
  expect(templateMentionSuggestions(templates, '+small')).toEqual([]);
  expect(templateMentionSuggestions(templates, 'missing')).toEqual([]);
});

it('recovers template chips and extracts only complete template references, including repetitions', () => {
  expect(sentMentionParts('Use @`template:small-change`')).toEqual([
    { text: 'Use ', label: null }, { text: '@`template:small-change`', label: 'small-change' },
  ]);
  expect(extractTemplateMentions('unfinished @`template:oops then @`template:first` @`` template:second `` @`template:first`'))
    .toEqual(['first', 'second', 'first']);
  for (const text of ['template:plain', '@:small', '@`template:`', '@`template:two words`', '@`template:unfinished', '@`tag:a`']) {
    expect(extractTemplateMentions(text)).toEqual([]);
  }
});

describe('selectedReadyTemplateId', () => {
  const ready = {
    id: 'dev',
    title: 'Development',
    description: null,
    availability: { status: 'ready' as const },
    tasks: [],
  };
  const needsPlugin = {
    id: 'deploy',
    title: 'Deploy',
    description: null,
    availability: { status: 'requires_plugin' as const, plugin_id: 'git', plugin_name: 'Git' },
    tasks: [],
  };
  const withInput = {
    id: 'form',
    title: 'Form',
    description: null,
    availability: { status: 'ready' as const },
    input_schema: { type: 'object' },
    tasks: [],
  };

  it('returns the id only for a single known ready template without input_schema', () => {
    expect(selectedReadyTemplateId('Run @`template:dev`', [ready, needsPlugin, withInput])).toBe('dev');
    expect(selectedReadyTemplateId('Run @`template:deploy`', [ready, needsPlugin, withInput])).toBeUndefined();
    expect(selectedReadyTemplateId('Run @`template:form`', [ready, needsPlugin, withInput])).toBeUndefined();
    expect(selectedReadyTemplateId('Run @`template:unknown`', [ready, needsPlugin, withInput])).toBeUndefined();
    expect(selectedReadyTemplateId('Run @`template:dev` and @`template:deploy`', [ready, needsPlugin, withInput])).toBeUndefined();
    expect(selectedReadyTemplateId('Run @`template:dev` and @`template:dev`', [ready, needsPlugin, withInput])).toBeUndefined();
    expect(selectedReadyTemplateId('No templates', [ready])).toBeUndefined();
  });
});
