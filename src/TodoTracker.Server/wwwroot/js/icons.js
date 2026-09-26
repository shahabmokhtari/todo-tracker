// Minimal stroke icon set (24×24, 1.75 stroke). Built with DOM APIs so the strict CSP needs no inline markup.
const SVG = 'http://www.w3.org/2000/svg';

const PATHS = {
  check: ['M20 6 9 17l-5-5'],
  clock: ['M12 7v5l3 2', 'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z'],
  bellOff: ['M9.5 19a2.5 2.5 0 0 0 5 0', 'M6 8.5a6 6 0 0 1 9.3-5M18 8.5c0 7 3 9 3 9H6', 'M3 3l18 18'],
  pencil: ['M4 20h4L19 9l-4-4L4 16v4Z', 'M14 6l4 4'],
  play: ['M8 5v14l11-7-11-7Z'],
  pause: ['M8 5v14', 'M16 5v14'],
  skip: ['M6 5v14l9-7-9-7Z', 'M18 5v14'],
  reset: ['M3 12a9 9 0 1 0 3-6.7', 'M3 4v5h5'],
  plus: ['M12 5v14', 'M5 12h14'],
  x: ['M6 6l12 12', 'M18 6 6 18'],
  undo: ['M9 14 4 9l5-5', 'M4 9h11a5 5 0 0 1 0 10h-2'],
  report: ['M7 3h7l5 5v13H7V3Z', 'M14 3v5h5', 'M10 13h6M10 17h6'],
  sparkles: ['M12 3l1.8 4.7L18.5 9.5l-4.7 1.8L12 16l-1.8-4.7L5.5 9.5l4.7-1.8L12 3Z', 'M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8L19 15Z'],
  link: ['M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1', 'M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'],
  flag: ['M5 21V4', 'M5 4h11l-2 4 2 4H5'],
  layers: ['M12 3 3 8l9 5 9-5-9-5Z', 'M3 13l9 5 9-5'],
  chevron: ['M9 6l6 6-6 6'],
  more: ['M5 12h.01M12 12h.01M19 12h.01'],
  target: ['M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z', 'M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z', 'M12 12h.01'],
  note: ['M5 4h14v12l-4 4H5V4Z', 'M15 20v-4h4', 'M9 9h6M9 13h4'],
  lock: ['M6 11h12v10H6V11Z', 'M8 11V8a4 4 0 0 1 8 0v3'],
  trash: ['M4 7h16', 'M10 11v6M14 11v6', 'M6 7l1 13h10l1-13', 'M9 7V4h6v3'],
};

export function icon(name, { size = 18, className = '' } = {}) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', '1.75');
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('class', `icon ${className}`.trim());
  for (const d of PATHS[name] ?? []) {
    const path = document.createElementNS(SVG, 'path');
    path.setAttribute('d', d);
    svg.append(path);
  }
  return svg;
}

/** Circular progress ring (0..1), used for the focus timer and rollout step progress. */
export function ring(fraction, { size = 32, stroke = 3, className = '' } = {}) {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('class', `ring ${className}`.trim());
  svg.setAttribute('aria-hidden', 'true');
  const track = document.createElementNS(SVG, 'circle');
  const bar = document.createElementNS(SVG, 'circle');
  for (const el of [track, bar]) {
    el.setAttribute('cx', String(size / 2));
    el.setAttribute('cy', String(size / 2));
    el.setAttribute('r', String(r));
    el.setAttribute('fill', 'none');
    el.setAttribute('stroke-width', String(stroke));
  }
  track.setAttribute('class', 'ring-track');
  bar.setAttribute('class', 'ring-bar');
  bar.setAttribute('stroke-linecap', 'round');
  bar.setAttribute('stroke-dasharray', String(c));
  bar.setAttribute('stroke-dashoffset', String(c * (1 - Math.max(0, Math.min(1, fraction)))));
  bar.setAttribute('transform', `rotate(-90 ${size / 2} ${size / 2})`);
  svg.append(track, bar);
  return svg;
}
