import { useState, type FormEvent } from 'react';
import type { ShoppingItemView } from '../conversation/events';

interface Props {
  items: ShoppingItemView[];
  add: (text: string) => void;
  tick: (id: string, got: boolean) => void;
  remove: (id: string) => void;
  clearGot: () => void;
}

/**
 * The shopping list, under the familiar like the timers: what's still to get, ticked off as it's
 * got (by voice - "I got the milk" - or here), struck through until cleared. Empty, it's a link to
 * start one; the familiar fills it by voice too ("add milk").
 */
export function ShoppingPanel({ items, add, tick, remove, clearGot }: Props) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const toGet = items.filter((i) => !i.gotAt).length;
  const got = items.length - toGet;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!text.trim()) return;
    add(text.trim());
    setText('');
  }

  if (items.length === 0 && !open) {
    return (
      <button type="button" className="link shopping-start" onClick={() => setOpen(true)}>
        Start a shopping list
      </button>
    );
  }

  return (
    <section className="shopping" aria-label="Shopping list">
      <h3>
        Shopping list
        <span className="muted">{toGet === 0 ? ' · all got' : ` · ${toGet} to get`}</span>
      </h3>
      <ul>
        {items.map((item) => (
          <li key={item.id} className={item.gotAt ? 'got' : undefined}>
            <label>
              <input type="checkbox" checked={!!item.gotAt} onChange={(e) => tick(item.id, e.target.checked)} />
              <span>{item.text}</span>
            </label>
            <button type="button" className="link item-remove" onClick={() => remove(item.id)} aria-label={`Take ${item.text} off the list`}>
              ✕
            </button>
          </li>
        ))}
      </ul>
      <form className="shopping-add" onSubmit={submit}>
        <input value={text} onChange={(e) => setText(e.target.value)} placeholder="Add something…" maxLength={120} aria-label="Add to the shopping list" />
        <button type="submit" className="secondary" disabled={!text.trim()}>Add</button>
      </form>
      {got > 0 && (
        <button type="button" className="link" onClick={clearGot}>Clear what’s got ({got})</button>
      )}
    </section>
  );
}
