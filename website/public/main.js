// The little "Do this now" card in the hero: Mark done or Later brings the next task, like the app does.
// The page works without it; this only adds the buttons.
(() => {
  const card = document.getElementById('now-card');
  if (!card) return;
  const title = document.getElementById('now-title');
  const meta = document.getElementById('now-meta');
  const note = document.getElementById('now-note');
  const status = document.getElementById('now-status');
  const actions = document.getElementById('now-actions');
  const hint = document.getElementById('demo-hint');

  const tasks = [
    { title: 'Reply to the security review', chips: [['due 5 pm', 'warn'], ['#security']], note: 'Two findings left. Draft replies in the doc.' },
    { title: "Review Maya's API design doc", chips: [['⚡⚡'], ['#api']], note: 'Agent: claude · Pulled the open comments into a list.' },
    { title: 'Renew passport', chips: [['due Fri', 'warn'], ['#admin']], note: 'Photos are done. Book the appointment.' },
    { title: 'Prep 1:1 agenda', chips: [['15 min'], ['#team']], note: 'Ask about the offsite dates.' },
  ];
  let i = 0;
  let done = 0;
  const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function render() {
    const t = tasks[i % tasks.length];
    title.textContent = t.title;
    meta.replaceChildren(...t.chips.flatMap(([text, kind], n) => {
      const span = document.createElement('span');
      span.className = kind ? `chip chip-${kind}` : 'chip';
      span.textContent = text;
      return n ? [document.createTextNode(' '), span] : [span];
    }));
    note.textContent = t.note;
  }

  function next(message) {
    const swap = () => { i += 1; render(); card.classList.remove('is-changing'); status.textContent = message; };
    if (reduce) { swap(); return; }
    card.classList.add('is-changing');
    setTimeout(swap, 220);
  }

  actions.addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-act]');
    if (!btn) return;
    const current = tasks[i % tasks.length].title;
    if (btn.dataset.act === 'done') {
      done += 1;
      next(`Done: ${current}. ${done === 1 ? 'Nice start!' : `${done} done. Keep it going!`}`);
    } else {
      next(`${current} is out of sight until tomorrow 9:00.`);
    }
  });

  actions.hidden = false;
  hint.hidden = false;
})();
