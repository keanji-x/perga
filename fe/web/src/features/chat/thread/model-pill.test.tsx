// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { AgentProvider } from '../../../../../core/api/schemas.ts';
import type { ProviderAvailability } from '../../../../../core/domain/agent-providers.ts';
import { FOLLOW_INSTALLATION_DEFAULT } from '../../../../../core/domain/conversation.ts';
import { modelCatalogOperation, type ModelCatalog } from '../../../../../core/domain/conversation-models.ts';
import { ModelPill, PlannerRolePill } from './model-pill.tsx';
import { type ModelGroup } from './model-selection.ts';

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
  return { provider, status: 'not_configured', reason: 'calm-server was started without --claude-planner-config', authentication_method: null, authentication_notice: null, checked_at_ms: 1 };
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

function openMenu(name: RegExp): HTMLElement {
  fireEvent.click(trigger(name));
  return screen.getByRole('dialog');
}

describe('ModelPill', () => {
  it('keeps the draft effort on same-model clicks and clears it only on Default or model change', () => {
    const choose = vi.fn();
    const group = { provider: 'opencode' as const, availability: null, catalog: catalog({
      models: [
        model({ default_reasoning_effort: 'low' }),
        model({ id: 'second', model: 'second', display_name: 'Second', default_reasoning_effort: null }),
      ],
    }) };
    render(<ModelPill provider="opencode" groups={[group]} selection={{ model: 'gpt-5', reasoning_effort: 'high' }}
      onChange={choose} />);
    let menu = within(openMenu(/^Model:/));
    fireEvent.click(menu.getByRole('radio', { name: /^GPT-5/ }));
    expect(menu.getByRole('slider').getAttribute('aria-valuetext')).toBe('high');
    fireEvent.click(menu.getByRole('button', { name: 'Save' }));
    expect(choose).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'high' }, 'opencode');

    menu = within(openMenu(/^Model:/));
    fireEvent.keyDown(menu.getByRole('slider'), { key: 'Home' });
    fireEvent.click(menu.getByRole('button', { name: 'Save' }));
    expect(choose).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'opencode');

    menu = within(openMenu(/^Model:/));
    fireEvent.click(menu.getByRole('radio', { name: 'Second' }));
    fireEvent.click(menu.getByRole('button', { name: 'Save' }));
    expect(choose).toHaveBeenLastCalledWith({ model: 'second', reasoning_effort: null }, 'opencode');
  });

  it('allows Default to clear a saved effort when the provider lists no efforts', () => {
    const choose = vi.fn();
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null,
      catalog: catalog({ models: [model({ supported_reasoning_efforts: [], default_reasoning_effort: null })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: 'high' }} onChange={choose} />);
    const menu = within(openMenu(/^Model:/));
    const slider = menu.getByRole('slider', { name: 'Reasoning effort' });
    expect(slider.getAttribute('aria-valuetext')).toBe('high (Unavailable)');
    fireEvent.keyDown(slider, { key: 'Home' });
    expect(slider.getAttribute('aria-valuetext')).toBe('Default');
    fireEvent.click(menu.getByRole('button', { name: 'Save' }));
    expect(choose).toHaveBeenCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'codex');
  });

  it('announces Default for unset effort when the model declares no default', () => {
    render(<ModelPill provider="codex"
      groups={[{ provider: 'codex', availability: null, catalog: catalog({ models: [model({
        supported_reasoning_efforts: [{ reasoning_effort: 'low', description: null }, { reasoning_effort: 'high', description: null }],
        default_reasoning_effort: null,
      })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={vi.fn()} />);
    const menu = openMenu(/^Model:/);
    expect(within(menu).getByRole('slider', { name: 'Reasoning effort' }).getAttribute('aria-valuetext')).toBe('Default');
  });

  it.each([1, 2])('keeps null on Save and Cancel, selects low, and clears to Default (%s efforts)', count => {
    const onChange = vi.fn();
    render(<ModelPill provider="codex"
      groups={[{ provider: 'codex', availability: null, catalog: catalog({ models: [model({
        supported_reasoning_efforts: ['low', 'high'].slice(0, count).map(reasoning_effort => ({ reasoning_effort, description: null })),
        default_reasoning_effort: null,
      })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={onChange} />);
    let menu = openMenu(/^Model:/);
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'codex');
    onChange.mockClear();
    menu = openMenu(/^Model:/);
    fireEvent.keyDown(within(menu).getByRole('slider'), { key: 'ArrowRight' });
    expect(within(menu).getByRole('slider').getAttribute('aria-valuetext')).toBe('low');
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'low' }, 'codex');
    menu = openMenu(/^Model:/);
    fireEvent.keyDown(within(menu).getByRole('slider'), { key: 'ArrowRight' });
    fireEvent.keyDown(within(menu).getByRole('slider'), { key: 'Home' });
    expect(within(menu).getByRole('slider').getAttribute('aria-valuetext')).toBe('Default');
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: null }, 'codex');
    onChange.mockClear();
    menu = openMenu(/^Model:/);
    fireEvent.keyDown(within(menu).getByRole('slider'), { key: 'End' });
    fireEvent.click(within(menu).getByRole('button', { name: 'Cancel' }));
    menu = openMenu(/^Model:/);
    expect(within(menu).getByRole('slider').getAttribute('aria-valuetext')).toBe('Default');
    expect(onChange).not.toHaveBeenCalled();
  });

  it.each([null, 'unadvertised'])('keeps an unset unadvertised default separate from low (%s)', defaultEffort => {
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null,
      catalog: catalog({ models: [model({ default_reasoning_effort: defaultEffort })] }) }]}
      selection={{ model: 'gpt-5', reasoning_effort: null }} onChange={vi.fn()} />);
    expect(within(openMenu(/^Model:/)).getByRole('slider').getAttribute('aria-valuetext')).toBe('Default');
  });

  it('preserves an unadvertised explicit effort on untouched Save and offers supported replacements', () => {
    const onChange = vi.fn();
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]}
      selection={{ model: 'gpt-5', reasoning_effort: 'future' }} onChange={onChange} />);
    let menu = openMenu(/^Model:/);
    expect(within(menu).getByRole('slider').getAttribute('aria-valuetext')).toBe('future (Unavailable)');
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'future' }, 'codex');
    menu = openMenu(/^Model:/);
    fireEvent.keyDown(within(menu).getByRole('slider'), { key: 'End' });
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'high' }, 'codex');
  });

  it('preserves explicit null installation metadata instead of inheriting the model default', () => {
    const declared = catalog({ models: [model({ default_reasoning_effort: 'low' })],
      default: { model: 'gpt-5', reasoning_effort: null, supported_reasoning_efforts: [{ reasoning_effort: 'low', description: null }] } });
    render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: declared }]}
      selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />);
    expect(trigger(/^Model:/).textContent).not.toContain('·');
    expect(within(openMenu(/^Model:/)).getByRole('slider').getAttribute('aria-valuetext')).toBe('Default');
  });

  it('names the default it is actually following, and includes effort when provided', () => {
    render(
      <ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
    expect(trigger(/^Model:/).textContent).toBe('gpt-5-codex');
  });

  it('still says, to a screen reader, that it is following the default', () => {
    const { rerender } = render(
      <ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
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

  it('renders unified two-column layout with Providers on the left and Models on the right', () => {
    render(
      <ModelPill provider="codex" groups={both()} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
    const menu = openMenu(/^Model:/);
    const providers = within(menu).getByRole('group', { name: 'Providers' });
    const models = within(menu).getByRole('group', { name: 'Models' });
    expect(within(providers).getByRole('radio', { name: 'Codex' })).toBeTruthy();
    expect(within(providers).getByRole('radio', { name: 'Claude' })).toBeTruthy();
    expect(within(models).getByText('Default (gpt-5-codex)')).toBeTruthy();
  });

  it('switches right-column models when clicking another provider on the left, then emits model + provider on pick', () => {
    const onChange = vi.fn();
    render(
      <ModelPill provider="codex" groups={both()} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={onChange} />,
    );
    const menu = openMenu(/^Model:/);
    const providers = within(menu).getByRole('group', { name: 'Providers' });
    fireEvent.click(within(providers).getByRole('radio', { name: 'Claude' }));
    expect(onChange).not.toHaveBeenCalled();

    const models = within(menu).getByRole('group', { name: 'Models' });
    fireEvent.click(within(models).getByRole('radio', { name: /^Sonnet/ }));
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenCalledWith({ model: 'sonnet', reasoning_effort: null }, 'claude');
  });

  it('offers a distinct Default plus ordered explicit effort choices', () => {
    const onChange = vi.fn();
    const multiEffortCatalog = catalog({
      models: [model({
        supported_reasoning_efforts: [
          { reasoning_effort: 'low', description: null },
          { reasoning_effort: 'medium', description: null },
          { reasoning_effort: 'high', description: null },
        ],
        default_reasoning_effort: 'medium',
      })],
    });
    const { rerender } = render(
      <ModelPill
        provider="codex"
        groups={[{ provider: 'codex', availability: null, catalog: multiEffortCatalog }]}
        selection={{ model: 'gpt-5', reasoning_effort: null }}
        onChange={onChange}
      />,
    );
    const menu = openMenu(/^Model:/);
    expect(within(menu).getByText('Default (medium)')).toBeTruthy();
    const slider = within(menu).getByRole('slider', { name: 'Reasoning effort' });
    expect(slider.getAttribute('aria-valuetext')).toBe('Default (medium)');

    fireEvent.keyDown(slider, { key: 'End' });
    expect(within(menu).getByText('high')).toBeTruthy();
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'high' }, 'codex');

    rerender(
      <ModelPill
        provider="codex"
        groups={[{ provider: 'codex', availability: null, catalog: multiEffortCatalog }]}
        selection={{ model: 'gpt-5', reasoning_effort: 'low' }}
        onChange={onChange}
      />,
    );
    const menu2 = openMenu(/^Model:/);
    const slider2 = within(menu2).getByRole('slider', { name: 'Reasoning effort' });
    expect(slider2.getAttribute('aria-valuetext')).toBe('low');

    fireEvent.keyDown(slider2, { key: 'ArrowRight' });
    fireEvent.click(within(menu2).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'medium' }, 'codex');

    rerender(
      <ModelPill
        provider="codex"
        groups={[{ provider: 'codex', availability: null, catalog: multiEffortCatalog }]}
        selection={{ model: 'gpt-5', reasoning_effort: 'medium' }}
        onChange={onChange}
      />,
    );
    const menu3 = openMenu(/^Model:/);
    fireEvent.keyDown(within(menu3).getByRole('slider', { name: 'Reasoning effort' }), { key: 'ArrowLeft' });
    fireEvent.click(within(menu3).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: 'gpt-5', reasoning_effort: 'low' }, 'codex');
  });

  it('discards draft effort and model changes when Cancel is clicked', () => {
    const onChange = vi.fn();
    const multiEffortCatalog = catalog({
      models: [model({
        supported_reasoning_efforts: [
          { reasoning_effort: 'low', description: null },
          { reasoning_effort: 'medium', description: null },
          { reasoning_effort: 'high', description: null },
        ],
        default_reasoning_effort: 'medium',
      })],
    });
    render(
      <ModelPill
        provider="codex"
        groups={[{ provider: 'codex', availability: null, catalog: multiEffortCatalog }]}
        selection={{ model: 'gpt-5', reasoning_effort: 'low' }}
        onChange={onChange}
      />,
    );
    const menu = openMenu(/^Model:/);
    const slider = within(menu).getByRole('slider', { name: 'Reasoning effort' });
    fireEvent.keyDown(slider, { key: 'ArrowRight' });
    expect(within(menu).getByText('medium')).toBeTruthy();

    fireEvent.click(within(menu).getByRole('button', { name: 'Cancel' }));
    expect(onChange).not.toHaveBeenCalled();

    const reopened = openMenu(/^Model:/);
    expect(within(reopened).getByRole('slider', { name: 'Reasoning effort' }).getAttribute('aria-valuetext')).toBe('low');
  });

  it('disables the effort slider when a model provides no supported_reasoning_efforts', () => {
    render(
      <ModelPill
        provider="claude"
        groups={[{ provider: 'claude', availability: ready('claude'), catalog: claudeCatalog() }]}
        selection={{ model: 'haiku', reasoning_effort: null }}
        onChange={vi.fn()}
      />,
    );
    const menu = openMenu(/^Model:/);
    expect(within(menu).getByText('N/A')).toBeTruthy();
    const slider = within(menu).getByRole('slider', { name: 'Reasoning effort' });
    expect(isDisabled(slider)).toBe(true);
  });

  it('hides not_configured providers from the left column while keeping the active one', () => {
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
    const menu = openMenu(/^Model:/);
    const providers = within(menu).getByRole('group', { name: 'Providers' });
    expect(within(providers).queryByRole('radio', { name: 'Claude' })).toBeNull();
  });

  it('keeps an unavailable Codex group pickable under a warning when acceptsUnavailableBlankTrack is true', () => {
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
    const menu = openMenu(/^Model:/);
    fireEvent.click(within(menu).getByRole('radio', { name: 'Codex' }));
    const models = within(menu).getByRole('group', { name: 'Models' });
    expect(within(models).getByRole('note').textContent).toContain(reason);
    const defaultRow = within(models).getByRole('radio', { name: /^Default/ });
    expect(isDisabled(defaultRow)).toBe(false);
    fireEvent.click(defaultRow);
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
    expect(onChange).toHaveBeenLastCalledWith({ model: null, reasoning_effort: null }, 'codex');
  });

  it('blocks an unavailable Claude group and shows its unavailable reason', () => {
    const onChange = vi.fn();
    render(
      <ModelPill
        groups={both(claudeCatalog({ source: 'unavailable', models: [] }), unavailable('claude', CLAUDE_UNAVAILABLE))}
        provider="codex"
        selection={FOLLOW_INSTALLATION_DEFAULT}
        onChange={onChange}
      />,
    );
    const menu = openMenu(/^Model:/);
    fireEvent.click(within(menu).getByRole('radio', { name: 'Claude' }));
    const models = within(menu).getByRole('group', { name: 'Models' });
    expect(within(models).getByRole('note').textContent).toContain(CLAUDE_UNAVAILABLE);
    const defaultRow = within(models).getByRole('radio', { name: /^Default/ });
    expect(isDisabled(defaultRow)).toBe(true);
    fireEvent.click(defaultRow);
    expect(onChange).not.toHaveBeenCalled();
  });

  it('emits a managed provider and model without UI registration', () => {
    const onChange = vi.fn();
    const group: ModelGroup<'owner-backend'> = {
      provider: 'owner-backend',
      availability: null,
      catalog: catalog(),
      selectionProblem: null,
    };
    render(<ModelPill groups={[group]} provider="owner-backend" selection={FOLLOW_INSTALLATION_DEFAULT} onChange={onChange} />);
    const menu = openMenu(/^Model: owner-backend/);
    fireEvent.click(within(menu).getByRole('radio', { name: /^GPT-5/ }));
    fireEvent.click(within(menu).getByRole('button', { name: 'Save' }));
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
    expect(within(openMenu(/^Model:/)).getByText(/No models available on this account/)).toBeTruthy();
  });

  it('says why an unavailable catalog is empty in the words of its provider', () => {
    const unavailable = catalog({ models: [], source: 'unavailable', default_source: 'unknown' });
    const { unmount } = render(<ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: unavailable }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />);
    /* The menu cannot open while unavailable, but its rows are rendered; the text is what a Codex reader is told. */
    expect(document.body.textContent).toContain('codex is not running');
    unmount();
    render(<ModelPill provider="claude" groups={[{ provider: 'claude', availability: null, catalog: unavailable }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />);
    expect(document.body.textContent).toContain(CLAUDE_UNAVAILABLE);
    expect(document.body.textContent).not.toContain('codex is not running');
  });

  it('does not invent a provider compaction warning', () => {
    render(
      <ModelPill provider="codex" groups={[{ provider: 'codex', availability: null, catalog: catalog() }]} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />,
    );
    expect(within(openMenu(/^Model:/)).queryByRole('note')).toBeNull();
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
      {kind === 'model'
        ? <ModelPill provider="codex" groups={both()} selection={FOLLOW_INSTALLATION_DEFAULT} onChange={vi.fn()} />
        : <PlannerRolePill value="member" onChange={vi.fn()} />}
      <input aria-label="Next control" />
    </div>);
    const button = trigger(kind === 'model' ? /^Model:/ : /^Role:/);
    fireEvent.click(button);
    const popup = kind === 'model'
      ? screen.getByRole('dialog', { name: 'Select model and reasoning effort' })
      : screen.getByRole('menu');
    const frames = new Map<number, FrameRequestCallback>();
    let sequence = 0;
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => {
      frames.set(++sequence, callback);
      return sequence;
    });
    const cancel = vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(id => { frames.delete(id); });
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
