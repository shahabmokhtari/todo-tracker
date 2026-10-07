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
  external: ['M14 4h6v6', 'M20 4 11 13', 'M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5'],
  settings: ['M4 6h10M18 6h2', 'M4 12h4M12 12h8', 'M4 18h12M20 18h0', 'M16 4v4', 'M10 10v4', 'M18 16v4'],
  paperclip: ['M21 11.5 12.6 19.9a5 5 0 0 1-7.1-7.1l8.5-8.5a3.3 3.3 0 0 1 4.7 4.7l-8.5 8.5a1.7 1.7 0 0 1-2.4-2.4l7.8-7.8'],
  folder: ['M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'],
  alert: ['M12 3 2 20h20z', 'M12 10v4', 'M12 17h0'],
  search: ['M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z', 'M20 20l-4-4'],
  obsidian: ['M9 3 5 9l3 12 8-3 3-7-5-8z', 'M9 3l1 8-2 10', 'M10 11l9 0'],
  history: ['M3 12a9 9 0 1 0 3-6.7', 'M3 4v5h5', 'M12 7v5l3 2'],
  chat: ['M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z', 'M8.5 11h.01', 'M12 11h.01', 'M15.5 11h.01'],
  send: ['M22 2 11 13', 'M22 2l-7 20-4-9-9-4z'],
  stop: ['M6 6h12v12H6z'],
  cloud: ['M17.5 19H7a5 5 0 1 1 1.1-9.9A6 6 0 0 1 19.6 11 4 4 0 0 1 17.5 19z'],
  puzzle: ['M14 4a2 2 0 1 0-4 0v1H6a1 1 0 0 0-1 1v4h1a2 2 0 1 1 0 4H5v4a1 1 0 0 0 1 1h4v-1a2 2 0 1 1 4 0v1h4a1 1 0 0 0 1-1v-4h-1a2 2 0 1 1 0-4h1V6a1 1 0 0 0-1-1h-4z'],
  grip: ['M9 6h.01', 'M15 6h.01', 'M9 12h.01', 'M15 12h.01', 'M9 18h.01', 'M15 18h.01'],
  up: ['M12 19V5', 'M5 12l7-7 7 7'],
  down: ['M12 5v14', 'M19 12l-7 7-7-7'],
  sun: ['M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z', 'M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4'],
  moon: ['M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z'],
  tag: ['M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z', 'M7.5 7.5h.01'],
  kanban: ['M4 4h4v16H4z', 'M10 4h4v10h-4z', 'M16 4h4v7h-4z'],
  list: ['M8 6h12M8 12h12M8 18h12', 'M4 6h.01M4 12h.01M4 18h.01'],
  tree: ['M4 5h6', 'M8 5v14', 'M8 12h4', 'M8 19h4', 'M14 12h6', 'M14 19h6'],
  archive: ['M3 4h18v4H3z', 'M5 8v12h14V8', 'M10 12h4'],
  chart: ['M4 20V10', 'M10 20V4', 'M16 20v-7', 'M22 20H2'],
  pie: ['M21 12A9 9 0 1 1 12 3v9z', 'M14 2.5A9 9 0 0 1 21.5 10H14z'],
  menu: ['M4 6h16', 'M4 12h16', 'M4 18h16'],
  expand: ['M15 3h6v6', 'M9 21H3v-6', 'M21 3l-7 7', 'M3 21l7-7'],
  shrink: ['M4 14h6v6', 'M20 10h-6V4', 'M14 10l7-7', 'M3 21l7-7'],
  timer: ['M12 14V9', 'M9 2h6', 'M12 22a8 8 0 1 0 0-16 8 8 0 0 0 0 16z', 'M19 6l1.5-1.5'],
  coffee: ['M4 9h12v5a5 5 0 0 1-5 5H9a5 5 0 0 1-5-5V9z', 'M16 10h1.5a2.5 2.5 0 0 1 0 5H16', 'M8 2v3M12 2v3'],
  command: ['M9 6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3z'],
  calendar: ['M4 5h16v16H4z', 'M4 10h16', 'M8 3v4M16 3v4'],
  inbox: ['M3 12h5l2 3h4l2-3h5', 'M5 5h14l2 7v7H3v-7l2-7z'],
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
