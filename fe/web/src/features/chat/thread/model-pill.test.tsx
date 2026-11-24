// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { AgentProvider } from '../../../../../core/api/schemas.ts';
import type { ProviderAvailability } from '../../../../../core/domain/agent-providers.ts';
import { FOLLOW_INSTALLATION_DEFAULT } from '../../../../../core/domain/conversation.ts';
import { modelCatalogOperation, type ModelCatalog } from '../../../../../core/domain/conversation-models.ts';
import { ModelPill, PermissionModePill } from './model-pill.tsx';
import { scoreModelSearch, type ModelGroup } from './model-selection.ts';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

function model(overrides: Partial<ModelCatalog['models'][number]> = {}): ModelCatalog['models'][number] {
  return {
    id: 'preset-fast',
    model: 'gpt-5',
    resolved_model: null,
    display_name: 'GPT-5',
    description: 'The everyday one.',
    is_default: false,
    supported_reasoning_efforts: [
      { reasoning_effort: 'low', description: 'Answers sooner.' },
      { reasoning_effort: 'high', description: 'Thinks longer.' },
    ],
    default_reasoning_effort: 'low',
    ...overrides,
  };
}

function catalog(overrides: Partial<ModelCatalog> = {}): ModelCatalog {
  return {
    models: [model()],
    default: { model: 'gpt-5-codex', reasoning_effort: null, supported_reasoning_efforts: null },
    default_source: 'config_read',
    source: 'live',
    fetched_at_ms: 1_760_000_000_000,
    ...overrides,
  };
}

const CLAUDE_LEVELS = ['low', 'medium', 'high', 'xhigh', 'max'].map((level) => ({ reasoning_effort: level, description: null }));

function claudeEntry(value: string, resolved: string, name: string, levels = CLAUDE_LEVELS): ModelCatalog['models'][number] {
  return model({
    id: value, model: value, resolved_model: resolved, display_name: name, description: `${name}, as the CLI says.`,
    supported_reasoning_efforts: levels, default_reasoning_effort: null,
  });
}

function claudeCatalog(overrides: Partial<ModelCatalog> = {}): ModelCatalog {
  return {
    models: [
      claudeEntry('opus[1m]', 'claude-opus-5-5[1m]', 'Opus (1M context)'),
      claudeEntry('claude-fable-5-1[1m]', 'claude-fable-5-1', 'Fable'),
      claudeEntry('sonnet', 'claude-sonnet-5', 'Sonnet'),
      claudeEntry('haiku', 'claude-haiku-4-5-20251001', 'Haiku', []),
    ],
    default: { model: 'claude-opus-5-5[1m]', reasoning_effort: null, supported_reasoning_efforts: CLAUDE_LEVELS },
    default_source: 'claude_cli',
    source: 'live',
    fetched_at_ms: 1_760_000_000_000,
    ...overrides,
  };
}

const CLAUDE_UNAVAILABLE = 'Claude cannot run on this server right now';

function ready(provider: AgentProvider): ProviderAvailability {
  return { provider, status: 'ready', reason: null, authentication_method: null, authentication_notice: null, checked_at_ms: 1 };
}

function unavailable(provider: AgentProvider, reason: string): ProviderAvailability {
  return { provider, status: 'unavailable', reason, authentication_method: null, authentication_notice: null, checked_at_ms: 1 };
}

function notConfigured(provider: AgentProvider): ProviderAvailability {
  return { provider, status: 'not_configured', reason: 'Planner authentication is not configured', authentication_method: null, authentication_notice: null, checked_at_ms: 1 };
}

function both(
  claude: ModelCatalog | null = claudeCatalog(),
  claudeAvailability: ProviderAvailability | null = ready('claude'),
): readonly ModelGroup[] {
  return [
    { provider: 'codex', availability: ready('codex'), catalog: catalog() },
    { provider: 'claude', availability: claudeAvailability, catalog: claude },
  ];
}

function isDisabled(element: HTMLElement): boolean {
  return element.hasAttribute('disabled') || element.getAttribute('aria-disabled') === 'true';
}

function trigger(name: RegExp): HTMLElement {
  return screen.getByRole('button', { name });
}

function openMenu(name = /^Model:/): HTMLElement {
  fireEvent.click(trigger(name));
  return screen.getByRole('dialog');
}

function openModelsLevel(name = /^Model:/): HTMLElement {
  const dialog = openMenu(name);
  const drill = within(dialog).queryByRole('button', { name: /^Change model:/ });
  if (drill !== null) fireEvent.click(drill);
  return dialog;
}

describe('ModelPill', () => {
  it('opens Level 1 overview with instant effort controls and drills into Level 2 models without Save/Cancel', () => {
    const choose = vi.fn();
    const group = { provider: 'opencode' as const, availability: null, catalog: catalog({
      models: [
        model({ default_reasoning_effort: 'low' }),
        model({ id: 'second', model: 'second', display_name: 'Second', default_reasoning_effort: null }),
      ],
    }) };
    render(<ModelPill provider="opencode" groups={[group]} selection={{ model: 'gpt-5', reasoning_effort: 'high' }}
      onChange={choose} />);

    // Level 1 shows inline effort slider right beside the model summary with zero Save/Cancel/Reset chrome.
    const menu = within(openMenu());
    expect(menu.queryByRole('button', { name: 'Save' })).toBeNull();
    expect(menu.queryByRole('button', { name: 'Cancel' })).toBeNull();
    expect(menu.queryByRole('button', { name: /Reset/i })).toBeNull();
    const slider = menu.getByRole('slider', { name: 'Reasoning effort' });
    expect(slider.getAttribute('aria-valuetext')).toBe('high');

    // Sliding left changes effort immediately.
    fireEvent.keyDown(slider, { key: 'Home' });
    expect(choose).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'low' }, 'opencode');
    choose.mockClear();

    // Drilling into Level 2 and clicking already-selected model returns to Level 1 without clobbering effort.
    fireEvent.click(menu.getByRole('button', { name: /^Change model:/ }));
    fireEvent.click(menu.getByRole('radio', { name: /^GPT-5/ }));
    expect(choose).not.toHaveBeenCalled();
    expect(menu.getByRole('slider', { name: 'Reasoning effort' })).toBeTruthy();

    // Picking a different model in Level 2 emits immediately, transitions to Level 1 showing the new model,
    // and sliding effort immediately targets the newly picked model even before parent props catch up.
    fireEvent.click(menu.getByRole('button', { name: /^Change model:/ }));
    fireEvent.click(menu.getByRole('radio', { name: 'Second' }));
    expect(choose).toHaveBeenLastCalledWith({ model: 'second', reasoning_effort: null }, 'opencode');
    expect(menu.getByRole('button', { name: 'Change model: Second' })).toBeTruthy();
    fireEvent.keyDown(menu.getByRole('slider', { name: 'Reasoning effort' }), { key: 'End' });
    expect(choose).toHaveBeenLastCalledWith({ model: 'second', reasoning_effort: 'high' }, 'opencode');
  });

  it('allows clearing a saved effort when the provider lists no efforts', () => {
    const choose = vi.fn();
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null,
      catalog: catalog({ models: [model({ supported_reasoning_efforts: [], default_reasoning_effort: null })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: 'high' }} onChange={choose} />);
    const menu = within(openMenu());
    expect(menu.getByText('high')).toBeTruthy();
    fireEvent.click(menu.getByRole('button', { name: 'Clear unavailable effort' }));
    expect(choose).toHaveBeenCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'codex');
  });

  it('announces Default for unset effort when the model declares no default', () => {
    render(<ModelPill provider="codex"
      groups={[{ provider: 'codex', availability: null, catalog: catalog({ models: [model({
        supported_reasoning_efforts: [{ reasoning_effort: 'low', description: null }, { reasoning_effort: 'high', description: null }],
        default_reasoning_effort: null,
      })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={vi.fn()} />);
    const menu = within(openMenu());
    expect(menu.getByRole('slider', { name: 'Reasoning effort' }).getAttribute('aria-valuetext')).toBe('Default');
  });

  it('never adds a synthetic Default tick on the slider; selects explicit effort and supports Backspace to unset', () => {
    const onChange = vi.fn();
    const group = { provider: 'codex' as const, availability: null, catalog: catalog({ models: [model({
      supported_reasoning_efforts: [
        { reasoning_effort: 'low', description: null },
        { reasoning_effort: 'medium', description: null },
        { reasoning_effort: 'high', description: null },
      ],
      default_reasoning_effort: null,
    })] }) };
    const { rerender } = render(<ModelPill provider="codex" groups={[group]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={onChange} />);

    let menu = within(openMenu());
    const slider = menu.getByRole('slider', { name: 'Reasoning effort' });
    // Exactly 3 stops (0..2), no extra synthetic Default tick, while keeping ARIA aria-valuenow valid.
    expect(slider.getAttribute('aria-valuemax')).toBe('2');
    expect(slider.getAttribute('aria-valuetext')).toBe('Default');
    expect(slider.getAttribute('aria-valuenow')).toBe('0');

    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'low' }, 'codex');

    rerender(<ModelPill provider="codex" groups={[group]}
      selection={{ model: 'gpt-5', reasoning_effort: 'low' }} onChange={onChange} />);
    menu = within(screen.getByRole('dialog'));
    fireEvent.keyDown(menu.getByRole('slider', { name: 'Reasoning effort' }), { key: 'Backspace' });
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'codex');
  });

  it('uses an inline radio pair instead of a degenerate 1-tick slider when only 1 effort exists', () => {
    const onChange = vi.fn();
    const group = { provider: 'codex' as const, availability: null, catalog: catalog({ models: [model({
      supported_reasoning_efforts: [{ reasoning_effort: 'low', description: null }],
      default_reasoning_effort: null,
    })] }) };
    const { rerender } = render(<ModelPill provider="codex" groups={[group]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={onChange} />);
    const menu = within(openMenu());
    expect(menu.queryByRole('slider')).toBeNull();
    fireEvent.click(menu.getByRole('radio', { name: 'low' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'low' }, 'codex');

    rerender(<ModelPill provider="codex" groups={[group]}
      selection={{ model: 'gpt-5', reasoning_effort: 'low' }} onChange={onChange} />);
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('radio', { name: 'Default' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'codex');
  });

  it.each([null, 'unadvertised'])('keeps an unset unadvertised default separate from low (%s)', (defaultEffort) => {
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null,
      catalog: catalog({ models: [model({ default_reasoning_effort: defaultEffort })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={vi.fn()} />);
    const menu = within(openMenu());
    expect(menu.getByRole('slider', { name: 'Reasoning effort' }).getAttribute('aria-valuetext')).toBe('Default');
  });

  it('shows an unadvertised explicit effort as unavailable and offers supported replacements', () => {
    const onChange = vi.fn();
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]}
      selection={{ model: 'gpt-5', reasoning_effort: 'future' }} onChange={onChange} />);
    const menu = within(openMenu());
    const slider = menu.getByRole('slider', { name: 'Reasoning effort' });
    expect(slider.getAttribute('aria-valuetext')).toBe('future (Unavailable)');
    fireEvent.keyDown(slider, { key: 'End' });
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'high' }, 'codex');
  });

  it('preserves explicit null installation metadata instead of inheriting the model default', () => {
    const declared = catalog({ models: [model({ default_reasoning_effort: 'low' })],
      default: { model: 'gpt-5', reasoning_effort: null, supported_reasoning_efforts: [{ reasoning_effort: 'low', description: null }, { reasoning_effort: 'high', description: null }] } });
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: declared }]}
      selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />);
    expect(trigger(/^Model:/).textContent).not.toContain('·');
    expect(within(openMenu()).getByRole('slider', { name: 'Reasoning effort' }).getAttribute('aria-valuetext')).toBe('Default');
  });

  it('names the default it is actually following and includes active effort on the trigger', () => {
    const { rerender } = render(
      <ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
    expect(trigger(/^Model:/).textContent).toBe('gpt-5-codex');
    expect(trigger(/^Model:/).getAttribute('aria-label'))
      .toBe("Model: gpt-5-codex (this installation's default)");

    rerender(
      <ModelPill provider="codex"
        groups={[{ provider: 'codex', availability: null, catalog: catalog() }]}
        selection={{ model: 'gpt-5', reasoning_effort: null }}
        onChange={vi.fn()}
      />,
    );
    expect(trigger(/^Model:/).getAttribute('aria-label')).toBe('Model: GPT-5 · low');
  });

  it('skips Level 1 and opens models directly when the current model has no reasoning efforts', () => {
    render(
      <ModelPill
        provider="claude"
        groups={[{ provider: 'claude', availability: ready('claude'), catalog: claudeCatalog() }]}
        selection={{ model: 'haiku', reasoning_effort: null }}
        onChange={vi.fn()}
      />,
    );
    const menu = within(openMenu());
    expect(menu.queryByRole('slider', { name: 'Reasoning effort' })).toBeNull();
    expect(menu.queryByRole('button', { name: 'Back to model settings' })).toBeNull();
    expect(menu.getByRole('group', { name: 'Models' })).toBeTruthy();
  });

  it('shows a left provider rail for multiple providers and picks from the selected provider in one click', () => {
    const onChange = vi.fn();
    render(
      <ModelPill provider="codex" groups={both()} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={onChange} />,
    );
    const menu = within(openModelsLevel());
    const providers = within(menu.getByRole('group', { name: 'Providers' }));
    expect(providers.getByRole('radio', { name: 'Codex' }).getAttribute('aria-checked')).toBe('true');
    expect(providers.getByRole('radio', { name: 'Claude' }).getAttribute('aria-checked')).toBe('false');
    expect(within(menu.getByRole('group', { name: 'Models' })).getByTitle('gpt-5-codex')).toBeTruthy();
    expect(within(menu.getByRole('group', { name: 'Models' })).queryByRole('radio', { name: /^Sonnet/ })).toBeNull();

    fireEvent.click(providers.getByRole('radio', { name: 'Claude' }));
    expect(onChange).not.toHaveBeenCalled();

    const models = within(menu.getByRole('group', { name: 'Models' }));
    fireEvent.click(models.getByRole('radio', { name: /^Sonnet/ }));
    expect(onChange).toHaveBeenCalledWith({ model: 'sonnet', reasoning_effort: null }, 'claude');
  });

  it('selects another ready provider default after switching in the provider rail', () => {
    const onChange = vi.fn();
    render(
      <ModelPill provider="codex" groups={both()} selection={{ model: 'gpt-5', reasoning_effort: 'high' }} onChange={onChange} />,
    );
    const menu = within(openModelsLevel());
    fireEvent.click(within(menu.getByRole('group', { name: 'Providers' })).getByRole('radio', { name: 'Claude' }));
    const models = within(menu.getByRole('group', { name: 'Models' }));
    const claudeDefault = models.getAllByTitle('claude-opus-5-5[1m]')[0]?.closest('button') ?? null;
    expect(claudeDefault).not.toBeNull();
    fireEvent.click(claudeDefault!);
    expect(onChange).toHaveBeenCalledWith({ model: null, reasoning_effort: null }, 'claude');
  });

  it('omits the Default row for a non-active provider when defaultAvailable is false', () => {
    const onChange = vi.fn();
    render(
      <ModelPill
        provider="codex"
        groups={[
          { provider: 'codex', availability: ready('codex'), catalog: catalog() },
          { provider: 'claude', availability: ready('claude'), catalog: claudeCatalog(), defaultAvailable: false },
        ]}
        selection={{ model: 'gpt-5', reasoning_effort: 'high' }}
        onChange={onChange}
      />,
    );
    const menu = within(openModelsLevel());
    fireEvent.click(within(menu.getByRole('group', { name: 'Providers' })).getByRole('radio', { name: 'Claude' }));
    const models = within(menu.getByRole('group', { name: 'Models' }));
    expect(models.queryByRole('radio', { name: /^Default/ })).toBeNull();
    fireEvent.click(models.getByRole('radio', { name: /^Sonnet/ }));
    expect(onChange).toHaveBeenCalledWith({ model: 'sonnet', reasoning_effort: null }, 'claude');
  });

  it('hides not_configured providers while keeping the active one', () => {
    render(
      <ModelPill
        provider="codex"
        groups={[
          { provider: 'codex', availability: ready('codex'), catalog: catalog() },
          { provider: 'claude', availability: notConfigured('claude'), catalog: null },
        ]}
        selection={FOLLOW_INSTALLATION_DEFAULT}
        onChange={vi.fn()}
      />,
    );
    const menu = within(openModelsLevel());
    expect(menu.queryByRole('group', { name: 'Providers' })).toBeNull();
    expect(menu.queryByText('Claude')).toBeNull();
  });

  it('fuzzy-matches typos, punctuation-free queries, acronyms, and multi-token provider queries', () => {
    expect(scoreModelSearch(['Sonnet', 'sonnet', 'claude-sonnet-4-6', 'Claude'], 'sol')).toBeGreaterThan(0);
    expect(scoreModelSearch(['GPT-5.4', 'gpt-5.4', null, 'Codex'], 'gpt54')).toBeGreaterThan(0);
    expect(scoreModelSearch(['Opus (1M)', 'opus[1m]', 'claude-opus-4-6', 'Claude'], 'claude opus')).toBeGreaterThan(0);
    expect(scoreModelSearch(['Gemini 3 Pro', 'gemini-3-pro', null, 'AGY'], 'g3p')).toBeGreaterThan(0);
    expect(scoreModelSearch(['Sonnet', 'sonnet', 'claude-sonnet-4-6', 'Claude'], 'zzzzz')).toBe(0);
  });

  it('keeps an unavailable Codex group with models pickable under a warning when acceptsUnavailableBlankTrack is true', () => {
    const reason = 'codex is not logged in';
    const onChange = vi.fn();
    render(
      <ModelPill
        groups={[
          { provider: 'codex', availability: unavailable('codex', reason), catalog: catalog(), acceptsUnavailableBlankTrack: true },
          { provider: 'claude', availability: ready('claude'), catalog: claudeCatalog() },
        ]}
        provider="claude"
        selection={FOLLOW_INSTALLATION_DEFAULT}
        onChange={onChange}
      />,
    );
    const menu = within(openModelsLevel());
    fireEvent.click(within(menu.getByRole('group', { name: 'Providers' })).getByRole('radio', { name: 'Codex' }));
    const models = within(menu.getByRole('group', { name: 'Models' }));
    expect(models.getByRole('note').textContent).toContain(reason);
    const codexDefault = models.getByTitle('gpt-5-codex').closest('button')!;
    expect(isDisabled(codexDefault)).toBe(false);
    fireEvent.click(codexDefault);
    expect(onChange).toHaveBeenLastCalledWith({ model: null, reasoning_effort: null }, 'codex');
  });

  it('does not show a fake Default row for an unavailable provider with no models', () => {
    const reason = 'OpenCode CLI is not installed';
    const onChange = vi.fn();
    render(
      <ModelPill
        groups={[
          { provider: 'codex', availability: ready('codex'), catalog: catalog() },
          { provider: 'opencode', availability: unavailable('opencode', reason), catalog: catalog({ models: [], source: 'unavailable', default_source: 'unknown' }), acceptsUnavailableBlankTrack: true },
        ]}
        provider="codex"
        selection={FOLLOW_INSTALLATION_DEFAULT}
        onChange={onChange}
      />,
    );
    const menu = within(openModelsLevel());
    fireEvent.click(within(menu.getByRole('group', { name: 'Providers' })).getByRole('radio', { name: 'OpenCode' }));
    const models = within(menu.getByRole('group', { name: 'Models' }));
    expect(models.getByRole('note').textContent).toContain(reason);
    expect(models.queryByRole('radio', { name: /^Default/ })).toBeNull();
    fireEvent.click(models.getByRole('button', { name: 'Use OpenCode anyway' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: null, reasoning_effort: null }, 'opencode');
  });

  it('shows a blocked unavailable provider reason when selected in the rail and blocks picking from it', () => {
    const onChange = vi.fn();
    render(
      <ModelPill
        groups={both(claudeCatalog({ source: 'unavailable', models: [] }), unavailable('claude', CLAUDE_UNAVAILABLE))}
        provider="codex"
        selection={FOLLOW_INSTALLATION_DEFAULT}
        onChange={onChange}
      />,
    );
    const menu = within(openModelsLevel());
    expect(menu.queryByRole('note')).toBeNull();
    fireEvent.click(within(menu.getByRole('group', { name: 'Providers' })).getByRole('radio', { name: 'Claude' }));
    expect(menu.getByRole('note').textContent).toContain(CLAUDE_UNAVAILABLE);
    expect(menu.queryByRole('radio', { name: /^Default/ })).toBeNull();
    expect(menu.queryByRole('radio', { name: /^Sonnet/ })).toBeNull();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('emits a managed provider and model without UI registration in one click', () => {
    const onChange = vi.fn();
    const group: ModelGroup<'owner-backend'> = {
      provider: 'owner-backend',
      availability: null,
      catalog: catalog(),
      selectionProblem: null,
    };
    render(<ModelPill groups={[group]} provider="owner-backend" selection={FOLLOW_INSTALLATION_DEFAULT} onChange={onChange} />);
    const menu = within(openModelsLevel(/^Model: owner-backend/));
    fireEvent.click(menu.getByRole('radio', { name: /^GPT-5/ }));
    expect(onChange).toHaveBeenCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'owner-backend');
  });

  it('keeps fallback bootstrap catalog default_source as unknown and decodes ProviderModelInfo efforts', () => {
    const fallbackOp = modelCatalogOperation({ kind: 'provider', provider: 'claude' });
    const fallbackParsed = fallbackOp.responseSchema.parse({
      version: '1.0.0',
      providers: [{
        provider: 'claude',
        available: true,
        version: '2.0.0',
        reason: null,
        models: ['claude-opus-4-6', 'claude-sonnet-4-6'],
      }],
      templates: [],
      areas: [],
    });
    expect(fallbackParsed.default_source).toBe('unknown');
    expect(fallbackParsed.default.model).toBe('claude-opus-4-6');

    const richParsed = fallbackOp.responseSchema.parse({
      version: '1.0.0',
      providers: [{
        provider: 'claude',
        available: true,
        version: '2.0.0',
        reason: null,
        models: ['claude-opus-4-6', 'claude-sonnet-4-6'],
        modelCatalog: [
          {
            model: 'claude-opus-4-6',
            displayName: 'Claude Opus 4.6',
            description: '',
            isDefault: false,
            defaultReasoningEffort: 'high',
            supportedReasoningEfforts: [
              { reasoningEffort: 'low', description: '' },
              { reasoningEffort: 'high', description: '' },
            ],
          },
          {
            model: 'claude-sonnet-4-6',
            displayName: 'Claude Sonnet 4.6',
            description: '',
            isDefault: true,
            defaultReasoningEffort: 'medium',
            supportedReasoningEfforts: [
              { reasoningEffort: 'low', description: '' },
              { reasoningEffort: 'medium', description: '' },
              { reasoningEffort: 'high', description: '' },
            ],
          },
        ],
      }],
      templates: [],
      areas: [],
    });
    expect(richParsed.default.model).toBe('claude-sonnet-4-6');
    expect(richParsed.default.reasoning_effort).toBe('medium');

    const emptyParsed = fallbackOp.responseSchema.parse({
      version: '1.0.0',
      providers: [{
        provider: 'claude',
        available: true,
        version: '2.0.0',
        reason: null,
        models: [],
      }],
      templates: [],
      areas: [],
    });

    render(
      <ModelPill
        provider="claude"
        groups={[{ provider: 'claude', availability: ready('claude'), catalog: emptyParsed }]}
        selection={FOLLOW_INSTALLATION_DEFAULT}
        onChange={vi.fn()}
      />,
    );
    const live = trigger(/^Model:/);
    expect(live.hasAttribute('disabled') || live.getAttribute('aria-disabled') === 'true').toBe(false);
    expect(within(openMenu()).getByText(/No models available on this account/)).toBeTruthy();
  });

  it('says why an unavailable catalog is empty in the words of its provider', () => {
    const unavailableCatalog = catalog({ models: [], source: 'unavailable', default_source: 'unknown' });
    const { unmount } = render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: unavailableCatalog }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />);
    /* The menu cannot open while unavailable, but its rows are rendered; the text is what a Codex reader is told. */
    expect(document.body.textContent).toContain('codex is not running');
    unmount();
    render(<ModelPill provider="claude" groups={[{ provider: 'claude', availability: null, catalog: unavailableCatalog }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />);
    expect(document.body.textContent).toContain(CLAUDE_UNAVAILABLE);
    expect(document.body.textContent).not.toContain('codex is not running');
  });

  it('does not invent a provider compaction warning', () => {
    render(
      <ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
    expect(within(openModelsLevel()).queryByRole('note')).toBeNull();
    expect(screen.queryByRole('radio', { name: /smaller context window/ })).toBeNull();
  });

  it('shows the default and stays quiet while the catalog is still loading', () => {
    render(
      <ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: null }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
    expect(trigger(/^Model:/).textContent).toBe('Default');
    expect(trigger(/^Model:/).getAttribute('aria-label')).toBe('Model: Default');
  });
});

// Hold the production Escape frame so focus can move before it runs.
describe.each(['model', 'setting'] as const)('%s pill Escape focus', (kind) => {
  function setup() {
    const view = render(<div role="dialog" aria-label="Host dialog">
      {kind === 'setting'
        ? <PermissionModePill mode="default" options={[{ id: 'default', label: 'Default', description: 'Default approval policy', tone: 'neutral', isDefault: true }]} onChange={vi.fn()} />
        : <ModelPill provider="codex" groups={both()} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />}
      <input aria-label="Next control" />
    </div>);
    const button = trigger(kind === 'model' ? /^Model:/ : /^Approvals:/);
    fireEvent.click(button);
    const popup = kind === 'model'
      ? screen.getByRole('dialog', { name: 'Select model and reasoning effort' })
      : screen.getByRole('menu');
    const frames = new Map<number, FrameRequestCallback>();
    let sequence = 0;
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((callback) => {
      frames.set(++sequence, callback);
      return sequence;
    });
    const cancel = vi.spyOn(window, 'cancelAnimationFrame').mockImplementation((id) => { frames.delete(id); });
    const escape = () => fireEvent.keyDown(popup, { key: 'Escape' });
    const flush = () => {
      for (const [id, callback] of frames) {
        frames.delete(id);
        callback(0);
      }
    };
    return { ...view, button, popup, frames, cancel, escape, flush };
  }

  it('preserves focus moved outside the pill before the Escape frame runs', () => {
    const { escape, flush } = setup();
    escape();
    const next = screen.getByRole('textbox', { name: 'Next control' });
    next.focus();
    flush();
    expect(document.activeElement).toBe(next);
  });

  it('preserves focus moved elsewhere in a host dialog after Escape on the trigger', () => {
    const { button, flush } = setup();
    fireEvent.keyDown(button, { key: 'Escape' });
    const next = screen.getByRole('textbox', { name: 'Next control' });
    next.focus();
    flush();
    expect(document.activeElement).toBe(next);
  });

  it('restores trigger focus when focus remains on the body', () => {
    const { button, escape, flush } = setup();
    escape();
    (document.activeElement as HTMLElement).blur();
    expect(document.activeElement).toBe(document.body);
    flush();
    expect(document.activeElement).toBe(button);
  });

  it('restores trigger focus when focus remains in the closing popup', () => {
    const { button, popup, escape, flush } = setup();
    escape();
    // Astryx retains the closing content for its exit transition.
    const option = popup.querySelector<HTMLElement>('[role="radio"], [role="menuitem"]');
    expect(option).not.toBeNull();
    option!.focus();
    flush();
    expect(document.activeElement).toBe(button);
  });

  it('cancels the pending Escape frame on unmount', () => {
    const { escape, unmount, frames, cancel } = setup();
    escape();
    const pending = [...frames.keys()];
    expect(pending).toHaveLength(1);
    unmount();
    expect(cancel).toHaveBeenCalledWith(pending[0]);
    expect(frames.size).toBe(0);
  });

  it('cancels the pending Escape frame on reopen', () => {
    const { button, escape, frames, cancel } = setup();
    escape();
    const pending = [...frames.keys()];
    expect(pending).toHaveLength(1);
    fireEvent.click(button);
    expect(cancel).toHaveBeenCalledWith(pending[0]);
    expect(frames.has(pending[0])).toBe(false);
    expect(button.getAttribute('aria-expanded')).toBe('true');
  });

  it('cancels a pending restore on a subsequent Escape', () => {
    const { button, escape, frames, cancel } = setup();
    escape();
    const pending = [...frames.keys()];
    expect(pending).toHaveLength(1);
    fireEvent.keyDown(button, { key: 'Escape' });
    expect(cancel).toHaveBeenCalledWith(pending[0]);
    expect(frames.size).toBe(0);
  });
});
