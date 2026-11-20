import { useState } from '../../../ui/state/public.ts';
import { compilePergaTemplateInputs, parsePergaInputsSchema, type PergaTemplateRawValues } from '../../../../../core/domain/template-input.ts';
import styles from './protocol.module.css';

export function InputsPanel({ schema, inputs, onSave }: {
  schema: unknown; inputs: unknown; onSave?: (patch: Readonly<Record<string, unknown>>) => Promise<void>;
}) {
  const fields = parsePergaInputsSchema(schema);
  const [draft, setDraft] = useState<PergaTemplateRawValues>({});
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const current = typeof inputs === 'object' && inputs !== null ? inputs as Record<string, unknown> : {};
  const text = (value: unknown): string => typeof value === 'string' || typeof value === 'number' ? String(value) : '';
  const values = Object.fromEntries(fields.map(field => [field.key,
    draft[field.key] ?? (field.type === 'boolean' ? (current[field.key] ?? field.defaultValue) === true : text(current[field.key] ?? field.defaultValue ?? (field.type === 'enum' && !field.optional ? field.values[0] : ''))),
  ]));
  const compiled = compilePergaTemplateInputs(fields, values);
  if (fields.length === 0) return <p>No editable inputs.</p>;
  return <form className={styles.form} onSubmit={event => {
    event.preventDefault();
    if (!compiled.valid || !onSave || busy) return;
    setBusy(true); setMessage('');
    void onSave(compiled.input).then(() => { setDraft({}); setMessage('Inputs saved.'); }, (cause: unknown) => {
      setMessage(cause instanceof Error ? cause.message : 'Input update failed.');
    }).finally(() => setBusy(false));
  }}>
    {fields.map(field => <label key={field.key}>{field.label}
      {field.type === 'boolean' ? <input type="checkbox" checked={values[field.key] === true} disabled={busy || !onSave}
        onChange={event => setDraft({ ...draft, [field.key]: event.target.checked })} />
        : field.type === 'enum' ? <select value={String(values[field.key])} disabled={busy || !onSave} required={!field.optional}
          onChange={event => setDraft({ ...draft, [field.key]: event.target.value })}>
          {field.optional && <option value="">Choose…</option>}{field.values.map(value => <option key={value} value={value}>{value}</option>)}
        </select> : <input type={field.type === 'number' ? 'number' : 'text'} step={field.type === 'number' ? 'any' : undefined}
          value={String(values[field.key])} required={!field.optional} disabled={busy || !onSave}
          onChange={event => setDraft({ ...draft, [field.key]: event.target.value })} />}
      {compiled.errors[field.key] && <span>{compiled.errors[field.key]}</span>}
    </label>)}
    <button type="submit" disabled={busy || !compiled.valid || !onSave}>{busy ? 'Saving…' : 'Save inputs'}</button>
    {message && <p role="status">{message}</p>}
  </form>;
}
