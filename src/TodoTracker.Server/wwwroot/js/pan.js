// Drag the board's background to move around it, like a canvas: sideways across the columns, up and down the page.
// Only empty background starts a pan: cards (dragged to move them), buttons, fields and text stay as they are. Touch
// screens already scroll by dragging, so this is for the mouse and pen.

const INTERACTIVE = 'a, button, input, select, textarea, label, summary, [draggable="true"], [contenteditable], [role="button"], .kcard';
const THRESHOLD = 4;

/** The nearest ancestor that scrolls vertically (or the page). */
function verticalScroller(el) {
  for (let node = el.parentElement; node; node = node.parentElement) {
    const style = getComputedStyle(node);
    if (/(auto|scroll)/.test(style.overflowY) && node.scrollHeight > node.clientHeight) return node;
  }
  return document.scrollingElement ?? document.documentElement;
}

/** Whether a press on `target` (inside the board) may start a pan. */
export function canPanFrom(target, board) {
  if (!(target instanceof Element) || !board.contains(target)) return false;
  if (target.closest(INTERACTIVE)) return false;
  // Text being selected (a column title, an empty-column hint) wins over panning only when something is selected.
  return !window.getSelection()?.toString();
}

/** Makes `board` (a horizontally scrolling element) pannable by dragging its background. Returns a detach function. */
export function enablePan(board) {
  let pan = null;

  const down = (e) => {
    if (e.pointerType === 'touch' || e.button !== 0 || !canPanFrom(e.target, board)) return;
    pan = { id: e.pointerId, x: e.clientX, y: e.clientY, left: board.scrollLeft, scroller: verticalScroller(board), top: 0, moving: false };
    pan.top = pan.scroller.scrollTop;
  };

  const move = (e) => {
    if (!pan || e.pointerId !== pan.id) return;
    const dx = e.clientX - pan.x;
    const dy = e.clientY - pan.y;
    if (!pan.moving) {
      if (Math.abs(dx) < THRESHOLD && Math.abs(dy) < THRESHOLD) return;
      pan.moving = true;
      board.setPointerCapture?.(e.pointerId);
      board.classList.add('panning');
    }
    e.preventDefault();
    board.scrollLeft = pan.left - dx;
    pan.scroller.scrollTop = pan.top - dy;
  };

  const up = (e) => {
    if (!pan || e.pointerId !== pan.id) return;
    if (pan.moving) {
      board.releasePointerCapture?.(e.pointerId);
      board.classList.remove('panning');
      // The click that ends a pan isn't a click on whatever is under the pointer (only that one: it comes right away).
      const swallow = (c) => { c.stopPropagation(); c.preventDefault(); };
      board.addEventListener('click', swallow, { capture: true, once: true });
      setTimeout(() => board.removeEventListener('click', swallow, { capture: true }), 0);
    }
    pan = null;
  };

  board.addEventListener('pointerdown', down);
  board.addEventListener('pointermove', move);
  board.addEventListener('pointerup', up);
  board.addEventListener('pointercancel', up);
  return () => {
    board.removeEventListener('pointerdown', down);
    board.removeEventListener('pointermove', move);
    board.removeEventListener('pointerup', up);
    board.removeEventListener('pointercancel', up);
  };
}
