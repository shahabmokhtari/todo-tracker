// Charts for reports, drawn as SVG with DOM APIs (no library, nothing inline for the CSP). The geometry functions are
// pure (tested on their own); the builders turn them into accessible SVG (every mark has a <title>).

const SVG = 'http://www.w3.org/2000/svg';

/** A round axis maximum and its ticks for values up to `max` (at least `min`). */
export function niceScale(max, ticks = 4, min = 1) {
  const top = Math.max(max, min);
  const raw = top / ticks;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= raw);
  const end = Math.ceil(top / step) * step;
  return { max: end, ticks: Array.from({ length: Math.round(end / step) + 1 }, (_, i) => Math.round(i * step * 1000) / 1000) };
}

/** Bars for `values` (each an array of stacked parts) across `width`, scaled to `height` against `max`. */
export function barLayout(values, { width, height, max, gap = 0.25 }) {
  const n = Math.max(1, values.length);
  const slot = width / n;
  const w = Math.max(1, slot * (1 - gap));
  return values.map((parts, i) => {
    let y = height;
    return (Array.isArray(parts) ? parts : [parts]).map((v) => {
      const h = max > 0 ? (v / max) * height : 0;
      y -= h;
      return { x: i * slot + (slot - w) / 2, y, w, h };
    });
  });
}

/** An SVG path for a ring slice from angle a0 to a1 (radians, 0 = top, clockwise). */
export function arcPath(cx, cy, r, inner, a0, a1) {
  const full = a1 - a0 >= Math.PI * 2 - 1e-6;
  const end = full ? a0 + Math.PI * 2 - 1e-4 : a1;
  const pt = (radius, a) => [cx + radius * Math.sin(a), cy - radius * Math.cos(a)].map((v) => Math.round(v * 100) / 100);
  const large = end - a0 > Math.PI ? 1 : 0;
  const [x0, y0] = pt(r, a0);
  const [x1, y1] = pt(r, end);
  const [x2, y2] = pt(inner, end);
  const [x3, y3] = pt(inner, a0);
  return `M${x0} ${y0}A${r} ${r} 0 ${large} 1 ${x1} ${y1}L${x2} ${y2}A${inner} ${inner} 0 ${large} 0 ${x3} ${y3}Z`;
}

/** Slices of a donut: each value's share of the whole, as angles. */
export function donutSlices(values) {
  const total = values.reduce((s, v) => s + Math.max(0, v), 0);
  let a = 0;
  return values.map((v) => {
    const share = total > 0 ? Math.max(0, v) / total : 0;
    const slice = { start: a, end: a + share * Math.PI * 2, share };
    a = slice.end;
    return slice;
  });
}

/**
 * Timeline rows: each task's life (created → finished, due, or the end of the range) as a faint span, its work as
 * solid spans, and its deadline as a marker, all as x positions within `width` for the range [from, to).
 */
export function ganttLayout(rows, from, to, width) {
  const t0 = new Date(from).getTime();
  const t1 = new Date(to).getTime();
  const x = (t) => Math.max(0, Math.min(width, ((new Date(t).getTime() - t0) / (t1 - t0)) * width));
  return rows.map((row) => {
    const end = row.completedAt ?? row.deadline ?? to;
    return {
      row,
      life: { x: x(row.createdAt), w: Math.max(2, x(end) - x(row.createdAt)) },
      work: (row.work ?? []).map((w) => ({ x: x(w.start), w: Math.max(2, x(w.end) - x(w.start)), source: w.source })),
      deadline: row.deadline ? x(row.deadline) : null,
      done: row.completedAt ? x(row.completedAt) : null,
    };
  });
}

/** 0..4 intensity steps for heat maps (0 = nothing), relative to the busiest cell. */
export function heatLevel(value, max) {
  if (!value || !max) return 0;
  return Math.min(4, Math.max(1, Math.ceil((value / max) * 4)));
}

// ---- builders --------------------------------------------------------------------------------------------------

function el(tag, attrs = {}, ...children) {
  const node = document.createElementNS(SVG, tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v !== null && v !== undefined && v !== false) node.setAttribute(k, String(v));
  }
  for (const child of children) if (child) node.append(child);
  return node;
}

const title = (text) => el('title', {}, document.createTextNode(text));

/** A vertical bar chart: `series` is [{label, parts: [values], tip}], `colors` the part classes. */
export function barChart(series, { height = 180, format = (v) => String(v), partClasses = ['bar'], label = 'Chart' } = {}) {
  const width = 640;
  const pad = { l: 36, r: 8, t: 8, b: 22 };
  const totals = series.map((s) => s.parts.reduce((a, b) => a + b, 0));
  const scale = niceScale(Math.max(0, ...totals), 4, 1);
  const bars = barLayout(series.map((s) => s.parts), { width: width - pad.l - pad.r, height: height - pad.t - pad.b, max: scale.max });
  const svg = el('svg', { viewBox: `0 0 ${width} ${height}`, class: 'chart bars', role: 'img', 'aria-label': label });
  for (const t of scale.ticks) {
    const y = pad.t + (height - pad.t - pad.b) * (1 - t / scale.max);
    svg.append(el('line', { x1: pad.l, x2: width - pad.r, y1: y, y2: y, class: 'grid' }), el('text', { x: pad.l - 6, y: y + 4, class: 'axis', 'text-anchor': 'end' }, document.createTextNode(format(t))));
  }
  const every = Math.ceil(series.length / 10);
  bars.forEach((parts, i) => {
    const g = el('g', { class: 'bar-group' }, title(series[i].tip ?? `${series[i].label}: ${format(totals[i])}`));
    parts.forEach((p, k) => g.append(el('rect', { x: pad.l + p.x, y: pad.t + p.y, width: p.w, height: Math.max(0, p.h), rx: Math.min(3, p.w / 2), class: partClasses[k] ?? 'bar' })));
    svg.append(g);
    if (i % every === 0) svg.append(el('text', { x: pad.l + parts[0].x + parts[0].w / 2, y: height - 6, class: 'axis', 'text-anchor': 'middle' }, document.createTextNode(series[i].label)));
  });
  return svg;
}

/** A donut with a total in the middle: `items` is [{label, value, color}]. */
export function donutChart(items, { size = 180, thickness = 26, center = '', centerSub = '', label = 'Chart' } = {}) {
  const r = size / 2 - 2;
  const svg = el('svg', { viewBox: `0 0 ${size} ${size}`, class: 'chart donut', role: 'img', 'aria-label': label, width: size, height: size });
  const slices = donutSlices(items.map((i) => i.value));
  if (!items.length || slices.every((s) => s.share === 0)) {
    svg.append(el('circle', { cx: size / 2, cy: size / 2, r: r - thickness / 2, class: 'donut-empty', 'stroke-width': thickness, fill: 'none' }));
  }
  slices.forEach((s, i) => {
    if (s.share <= 0) return;
    svg.append(el('path', { d: arcPath(size / 2, size / 2, r, r - thickness, s.start, s.end), fill: items[i].color ?? 'currentColor', class: 'slice' }, title(`${items[i].label}: ${items[i].tip ?? Math.round(s.share * 100) + '%'}`)));
  });
  svg.append(el('text', { x: size / 2, y: size / 2 + (centerSub ? 0 : 6), 'text-anchor': 'middle', class: 'donut-center' }, document.createTextNode(center)));
  if (centerSub) svg.append(el('text', { x: size / 2, y: size / 2 + 18, 'text-anchor': 'middle', class: 'donut-sub' }, document.createTextNode(centerSub)));
  return svg;
}

/** A weekday × hour heat map (rows Monday–Sunday): `grid[weekday 0=Sun][hour]` in seconds. */
export function hourHeatmap(grid, { format = String, label = 'When you work' } = {}) {
  const cell = 18;
  const pad = { l: 34, t: 16 };
  const order = [1, 2, 3, 4, 5, 6, 0];
  const names = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  const max = Math.max(0, ...grid.flat());
  const svg = el('svg', { viewBox: `0 0 ${pad.l + 24 * (cell + 2)} ${pad.t + 7 * (cell + 2)}`, class: 'chart heat', role: 'img', 'aria-label': label });
  [0, 6, 12, 18].forEach((hr) => svg.append(el('text', { x: pad.l + hr * (cell + 2), y: 11, class: 'axis' }, document.createTextNode(`${hr}:00`))));
  order.forEach((day, row) => {
    svg.append(el('text', { x: pad.l - 6, y: pad.t + row * (cell + 2) + 13, class: 'axis', 'text-anchor': 'end' }, document.createTextNode(names[day])));
    grid[day].forEach((v, hr) => svg.append(el('rect', { x: pad.l + hr * (cell + 2), y: pad.t + row * (cell + 2), width: cell, height: cell, rx: 4, class: `heat-${heatLevel(v, max)}` }, title(`${names[day]} ${hr}:00 – ${format(v)}`))));
  });
  return svg;
}

/**
 * The task timeline (Gantt) as HTML: a label column and a track per task, with bars placed in percent, so it stays
 * crisp at any width. `rows` from the report; `from`/`to` the range; `colorOf(row)` the group color.
 */
export function ganttChart(rows, from, to, { colorOf = () => 'currentColor', format = String, onOpen = null, label = 'Timeline' } = {}) {
  const layout = ganttLayout(rows, from, to, 100);
  const pct = (v) => `${Math.round(v * 100) / 100}%`;
  const div = (cls, ...children) => {
    const node = document.createElement('div');
    node.className = cls;
    for (const child of children) if (child) node.append(child);
    return node;
  };
  const chart = div('gantt');
  chart.setAttribute('role', 'list');
  chart.setAttribute('aria-label', label);

  // Date ticks along the top.
  const t0 = new Date(from).getTime();
  const days = (new Date(to).getTime() - t0) / 86_400_000;
  const step = days > 120 ? 30 : days > 35 ? 7 : days > 10 ? 2 : 1;
  const scale = div('gantt-scale');
  for (let d = 0; d < days; d += step) {
    const tick = div('gantt-tick');
    tick.style.left = pct((d / days) * 100);
    tick.textContent = new Date(t0 + d * 86_400_000 + 43_200_000).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
    scale.append(tick);
  }

  chart.append(div('gantt-row gantt-head', div('gantt-name'), scale));
  for (const g of layout) {
    const color = colorOf(g.row);
    const spent = (g.row.work ?? []).reduce((s, w) => s + (new Date(w.end) - new Date(w.start)) / 1000, 0);
    const track = div('gantt-track');
    const life = div('gantt-life');
    life.style.left = pct(g.life.x);
    life.style.width = pct(Math.max(0.4, g.life.w));
    life.style.background = color;
    track.append(life);
    for (const w of g.work) {
      const bar = div(`gantt-work ${w.source}`);
      bar.style.left = pct(w.x);
      bar.style.width = pct(Math.max(0.35, w.w));
      bar.style.background = color;
      track.append(bar);
    }

    if (g.deadline !== null) {
      const due = div('gantt-deadline');
      due.style.left = pct(g.deadline);
      due.title = `Due ${new Date(g.row.deadline).toLocaleDateString()}`;
      track.append(due);
    }

    if (g.done !== null) {
      const done = div('gantt-done');
      done.style.left = pct(g.done);
      done.title = `Done ${new Date(g.row.completedAt).toLocaleDateString()}`;
      track.append(done);
    }

    const name = document.createElement(onOpen ? 'button' : 'span');
    name.className = 'gantt-name link';
    if (onOpen) {
      name.type = 'button';
      name.addEventListener('click', () => onOpen(g.row));
    }

    name.textContent = g.row.title;
    name.title = `${g.row.title}${spent ? ` · ${format(spent)} worked` : ''}${g.row.completedAt ? ' · done' : ''}`;
    const row = div('gantt-row', name, track);
    row.setAttribute('role', 'listitem');
    chart.append(row);
  }

  return chart;
}

/** Days as a calendar heat map (weeks as columns, like a contribution graph): `days` is [{date, value, tip}]. */
export function calendarHeatmap(days, { label = 'Activity' } = {}) {
  const cell = 12;
  const gap = 3;
  const first = days.length ? new Date(`${days[0].date}T12:00:00`) : new Date();
  const offset = (first.getDay() + 6) % 7; // weeks start on Monday
  const weeks = Math.ceil((days.length + offset) / 7);
  const max = Math.max(0, ...days.map((d) => d.value));
  const svg = el('svg', { viewBox: `0 0 ${weeks * (cell + gap)} ${7 * (cell + gap)}`, class: 'chart calendar', role: 'img', 'aria-label': label });
  days.forEach((d, i) => {
    const k = i + offset;
    svg.append(el('rect', { x: Math.floor(k / 7) * (cell + gap), y: (k % 7) * (cell + gap), width: cell, height: cell, rx: 3, class: `heat-${heatLevel(d.value, max)}` }, title(d.tip ?? `${d.date}: ${d.value}`)));
  });
  return svg;
}
