// Autosave: saves the latest value once typing pauses (or right away on flush), one save at a time, without ever
// losing what was typed while a save was in flight. Timers are injectable for tests.

/**
 * The fields of `next` that differ from `prev`, so a save only sends what this person changed and never puts back
 * values someone else changed meanwhile. `groups` lists fields that must travel together (e.g. a value and its clear flag).
 */
export function changedFields(prev, next, groups = Object.keys(next).map((k) => [k])) {
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  const out = {};
  for (const group of groups) {
    if (group.some((k) => !same(prev?.[k], next[k]))) group.forEach((k) => { out[k] = next[k]; });
  }
  return out;
}

export function createAutosave({ save, delay = 700, setTimer = setTimeout, clearTimer = clearTimeout, onState = () => {} }) {
  let pending = null; // { value } waiting to be saved
  let timer = null;
  let running = null; // promise of the save in flight
  let saved; // last value that reached the server
  let hasSaved = false;

  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);

  function arm(ms = delay) {
    if (timer !== null) clearTimer(timer);
    timer = setTimer(() => { timer = null; drain(); }, ms);
  }

  async function drain() {
    if (running || !pending) return running;
    const item = pending;
    const { value } = item;
    pending = null;
    onState('saving');
    running = (async () => {
      try {
        await save(value);
        saved = value;
        hasSaved = true;
        onState(pending ? 'pending' : 'saved');
      } catch (err) {
        if (!pending) pending = item;
        onState('error', err);
        throw err;
      } finally {
        running = null;
      }
    })();
    try {
      await running;
    } catch {
      return; // stays pending; the next edit or flush retries
    }
    if (pending && timer === null) arm();
  }

  return {
    /** Remember the newest value; it is saved when typing pauses. */
    schedule(value) {
      if (!pending && !running && hasSaved && same(value, saved)) return;
      pending = { value };
      onState('pending');
      arm();
    },
    /**
     * Save now (e.g. on Enter, blur, or closing the panel). Resolves to true once everything typed so far is saved,
     * or false when a save failed (the text stays pending, so nothing typed is dropped).
     */
    async flush() {
      if (timer !== null) { clearTimer(timer); timer = null; }
      while (running || pending) {
        if (running) await running.catch(() => {});
        else {
          const before = pending;
          await drain();
          if (pending === before) return false; // the save failed; keep it pending instead of looping
        }
      }
      return true;
    },
    /** Forget unsaved changes (e.g. the task was deleted). */
    cancel() {
      if (timer !== null) { clearTimer(timer); timer = null; }
      pending = null;
    },
    get dirty() { return pending !== null || running !== null; },
  };
}
