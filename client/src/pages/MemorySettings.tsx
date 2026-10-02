import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api } from '../api/client';

interface Memory {
  id: string;
  text: string;
  source: 'conversation' | 'told' | 'settings';
  createdAt: string;
  updatedAt: string;
}

const memoriesApi = {
  list: () => api<Memory[]>('/api/memories'),
  add: (text: string) => api<Memory>('/api/memories', { body: { text } }),
  update: (id: string, text: string) => api<Memory>(`/api/memories/${id}`, { method: 'PUT', body: { text } }),
  remove: (id: string) => api<void>(`/api/memories/${id}`, { method: 'DELETE' }),
  clear: () => api<void>('/api/memories', { method: 'DELETE' }),
};

const sourceText: Record<Memory['source'], string> = {
  conversation: 'picked up in conversation',
  told: 'you asked it to remember',
  settings: 'added here',
};

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });

function MemoryRow({ memory, onSaved, onDeleted }: { memory: Memory; onSaved: (m: Memory) => void; onDeleted: () => void }) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(memory.text);
  const [error, setError] = useState<string | null>(null);

  async function save(event: FormEvent) {
    event.preventDefault();
    try {
      onSaved(await memoriesApi.update(memory.id, text));
      setEditing(false);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Couldn’t save that.');
    }
  }

  if (editing) {
    return (
      <li>
        <form className="memory-edit" onSubmit={save}>
          <input value={text} onChange={(e) => setText(e.target.value)} maxLength={300} aria-label="Memory" autoFocus />
          <button type="submit">Save</button>
          <button type="button" className="secondary" onClick={() => { setEditing(false); setText(memory.text); }}>Cancel</button>
        </form>
        {error && <p className="error">{error}</p>}
      </li>
    );
  }

  return (
    <li>
      <div>
        <span>{memory.text}</span>
        <span className="muted">{sourceText[memory.source]} · {dateFormat.format(new Date(memory.updatedAt))}</span>
      </div>
      <span className="row-actions">
        <button type="button" className="link" onClick={() => setEditing(true)}>Edit</button>
        <button type="button" className="link" onClick={() => void memoriesApi.remove(memory.id).then(onDeleted)}>Forget</button>
      </span>
    </li>
  );
}

/**
 * Everything the familiar remembers (PLAN.md §6.6): picked up from conversations, or asked to
 * remember. All of it can be corrected or forgotten here, or by saying "forget that".
 */
export function MemorySettings() {
  const [memories, setMemories] = useState<Memory[] | null>(null);
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    memoriesApi.list().then(setMemories).catch((e: unknown) => setError(e instanceof Error ? e.message : 'Couldn’t load memories.'));
  }, []);
  useEffect(load, [load]);

  async function add(event: FormEvent) {
    event.preventDefault();
    setError(null);
    try {
      const memory = await memoriesApi.add(draft);
      setMemories((list) => [...(list ?? []), memory]);
      setDraft('');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Couldn’t add that.');
    }
  }

  async function clear() {
    if (!window.confirm('Forget everything? This can’t be undone.')) return;
    await memoriesApi.clear();
    setMemories([]);
  }

  if (!memories) return error ? <p className="error">{error}</p> : <p className="muted">Loading…</p>;

  return (
    <div className="stack">
      <p className="muted">
        Things it has picked up about you, so it doesn’t have to ask again. Say “remember…” or “forget…” to it, or change them here.
      </p>
      {memories.length === 0
        ? <p className="muted">Nothing yet.</p>
        : (
          <ul className="memory-list">
            {memories.map((m) => (
              <MemoryRow
                key={m.id}
                memory={m}
                onSaved={(saved) => setMemories((list) => list?.map((x) => (x.id === saved.id ? saved : x)) ?? null)}
                onDeleted={() => setMemories((list) => list?.filter((x) => x.id !== m.id) ?? null)}
              />
            ))}
          </ul>
        )}
      <form className="memory-edit" onSubmit={add}>
        <input value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="Add something, e.g. “I’m vegetarian”" maxLength={300} aria-label="New memory" />
        <button type="submit" disabled={!draft.trim()}>Add</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
      {memories.length > 0 && (
        <button type="button" className="link danger" onClick={() => void clear()}>Forget everything</button>
      )}
    </div>
  );
}
