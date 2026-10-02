// Draggable page outline over a photo: four corner handles plus one handle in the middle of each edge,
// so a curled or folded page can be followed, not just cropped. Coordinates are normalized (0-1) to the
// photo, exactly as the PC reports and accepts them.

const SVG_NS = 'http://www.w3.org/2000/svg';
const MID = 16;                  // middle sample of a 33-sample edge
const CORNER_NAMES = ['top left', 'top right', 'bottom right', 'bottom left'];
const EDGE_NAMES = ['top', 'right', 'bottom', 'left'];
// Which corner each edge starts and ends at (edges run in reading direction: top TL->TR, right TR->BR, bottom BL->BR, left TL->BL).
const EDGES = { top: [0, 1], right: [1, 2], bottom: [3, 2], left: [0, 3] };
const LOUPE_ZOOM = 3;

const clamp01 = value => Math.min(1, Math.max(0, value));
const copy = outline => JSON.parse(JSON.stringify({
  corners: outline.corners, top: outline.top, right: outline.right, bottom: outline.bottom, left: outline.left
}));

function element(name, attributes = {}) {
  const node = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
  return node;
}

export class OutlineEditor {
  constructor({ wrap, image, svg, loupe, onChange }) {
    this.wrap = wrap;
    this.image = image;
    this.svg = svg;
    this.loupe = loupe;
    this.onChange = onChange;
    this.outline = null;
    this.initial = null;
    this.drag = null;
    this.box = { x: 0, y: 0, w: 1, h: 1 };
    addEventListener('resize', () => this.layout());
  }

  /** Shows the photo and the outline over it. Resolves once the photo has loaded. */
  async load(url, outline) {
    this.image.src = url;
    try { await this.image.decode(); } catch { /* shown anyway once it loads */ }
    this.outline = copy(outline);
    this.initial = copy(outline);
    this.build();
    this.layout();
  }

  get current() { return copy(this.outline); }
  get changed() { return JSON.stringify(this.outline) !== JSON.stringify(this.initial); }

  reset() {
    this.outline = copy(this.initial);
    this.update();
    this.onChange?.();
  }

  /** Straight lines between the corners (what a flat page looks like). */
  straighten() {
    for (const [key, [a, b]] of Object.entries(EDGES)) {
      const start = this.outline.corners[a];
      const end = this.outline.corners[b];
      this.outline[key] = this.outline[key].map((_, i, all) => {
        const t = i / (all.length - 1);
        return [start[0] + (end[0] - start[0]) * t, start[1] + (end[1] - start[1]) * t];
      });
    }
    this.update();
    this.onChange?.();
  }

  // ---------- drawing ----------

  build() {
    this.svg.replaceChildren();
    this.shade = element('path', { class: 'shade' });
    this.line = element('path', { class: 'outline' });
    this.accent = element('path', { class: 'outline-accent' });
    this.svg.append(this.shade, this.line, this.accent);

    this.cornerHandles = this.outline.corners.map((_, index) => this.handle('corner', index, 12, 30, `Move ${CORNER_NAMES[index]} corner`));
    this.edgeHandles = EDGE_NAMES.map(key => this.handle('edge', key, 8.5, 26, `Bend ${key} edge`));
  }

  handle(kind, id, radius, hitRadius, label) {
    const group = element('g', { tabindex: '0', role: 'slider', 'aria-label': label, 'aria-valuetext': 'Use arrow keys to move' });
    group.append(
      element('circle', { class: 'hit', r: hitRadius }),
      element('circle', { class: kind === 'corner' ? 'dot' : 'edge-dot', r: radius })
    );
    group.addEventListener('pointerdown', event => this.begin(event, kind, id, group));
    group.addEventListener('pointermove', event => this.move(event));
    group.addEventListener('pointerup', event => this.end(event));
    group.addEventListener('pointercancel', event => this.end(event));
    group.addEventListener('keydown', event => this.nudge(event, kind, id));
    this.svg.append(group);
    return group;
  }

  /** Sizes the overlay to exactly the visible photo (the image is letterboxed inside its box). */
  layout() {
    if (!this.outline) return;
    const boxW = this.wrap.clientWidth;
    const boxH = this.wrap.clientHeight;
    const naturalW = this.image.naturalWidth || 1;
    const naturalH = this.image.naturalHeight || 1;
    const scale = Math.min(boxW / naturalW, boxH / naturalH);
    const w = naturalW * scale;
    const h = naturalH * scale;
    this.box = { x: (boxW - w) / 2, y: (boxH - h) / 2, w, h };
    this.svg.style.left = `${this.box.x}px`;
    this.svg.style.top = `${this.box.y}px`;
    this.svg.style.width = `${w}px`;
    this.svg.style.height = `${h}px`;
    this.svg.setAttribute('width', w);
    this.svg.setAttribute('height', h);
    this.update();
  }

  px(point) { return [point[0] * this.box.w, point[1] * this.box.h]; }

  update() {
    if (!this.outline) return;
    const { corners, top, right, bottom, left } = this.outline;
    const ring = [...top, ...right, ...[...bottom].reverse(), ...[...left].reverse()].map(point => this.px(point));
    const path = `M${ring.map(point => `${point[0].toFixed(1)} ${point[1].toFixed(1)}`).join('L')}Z`;
    this.shade.setAttribute('d', `M0 0H${this.box.w}V${this.box.h}H0Z${path}`);
    this.line.setAttribute('d', path);
    this.accent.setAttribute('d', path);

    corners.forEach((corner, index) => this.place(this.cornerHandles[index], corner));
    EDGE_NAMES.forEach((key, index) => this.place(this.edgeHandles[index], this.outline[key][MID]));
  }

  place(group, point) {
    const [x, y] = this.px(point);
    group.setAttribute('transform', `translate(${x.toFixed(1)} ${y.toFixed(1)})`);
  }

  // ---------- dragging ----------

  /** Pointer position as a normalized photo coordinate. */
  pointer(event) {
    const rect = this.svg.getBoundingClientRect();
    return [(event.clientX - rect.left) / rect.width, (event.clientY - rect.top) / rect.height];
  }

  begin(event, kind, id, group) {
    event.preventDefault();
    group.setPointerCapture(event.pointerId);
    const grabbed = kind === 'corner' ? this.outline.corners[id] : this.outline[id][MID];
    const at = this.pointer(event);
    this.drag = {
      kind, id, group,
      offset: [grabbed[0] - at[0], grabbed[1] - at[1]],     // keep the handle where it was grabbed, not under the finger's center
      origin: copy(this.outline)
    };
    this.showLoupe(grabbed);
  }

  move(event) {
    if (!this.drag) return;
    event.preventDefault();
    const at = this.pointer(event);
    const target = [clamp01(at[0] + this.drag.offset[0]), clamp01(at[1] + this.drag.offset[1])];
    const { kind, id, origin } = this.drag;
    const start = kind === 'corner' ? origin.corners[id] : origin[id][MID];
    const delta = [target[0] - start[0], target[1] - start[1]];
    if (kind === 'corner') this.applyCorner(id, delta, origin); else this.applyEdge(id, delta, origin);
    this.update();
    this.showLoupe(target);
    this.onChange?.();
  }

  end(event) {
    if (!this.drag) return;
    try { this.drag.group.releasePointerCapture(event.pointerId); } catch { /* already released */ }
    this.drag = null;
    this.loupe.hidden = true;
  }

  /** Moving a corner drags the two edges attached to it along, keeping their shape. */
  applyCorner(index, delta, origin) {
    const [x, y] = origin.corners[index];
    const moved = [clamp01(x + delta[0]), clamp01(y + delta[1])];
    const real = [moved[0] - x, moved[1] - y];
    this.outline.corners[index] = moved;
    for (const [key, [from, to]] of Object.entries(EDGES)) {
      if (from !== index && to !== index) continue;
      const samples = origin[key];
      this.outline[key] = samples.map((point, i) => {
        const t = i / (samples.length - 1);
        const weight = from === index ? 1 - t : t;
        return [clamp01(point[0] + real[0] * weight), clamp01(point[1] + real[1] * weight)];
      });
    }
  }

  /** Pulling an edge bends it smoothly; its two corners stay put. */
  applyEdge(key, delta, origin) {
    const samples = origin[key];
    this.outline[key] = samples.map((point, i) => {
      const t = i / (samples.length - 1);
      const weight = 1 - (2 * t - 1) ** 2;          // 0 at the corners, 1 in the middle
      return [clamp01(point[0] + delta[0] * weight), clamp01(point[1] + delta[1] * weight)];
    });
  }

  nudge(event, kind, id) {
    const step = event.shiftKey ? 0.02 : 0.004;
    const delta = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[event.key];
    if (!delta) return;
    event.preventDefault();
    const origin = copy(this.outline);
    if (kind === 'corner') this.applyCorner(id, delta, origin); else this.applyEdge(id, delta, origin);
    this.update();
    this.onChange?.();
  }

  // ---------- magnifier ----------

  /** A zoomed circle above the finger, so the exact paper edge can be placed without it being covered. */
  showLoupe(point) {
    const naturalW = this.image.naturalWidth;
    if (!naturalW) return;
    const size = this.loupe.clientWidth || 112;
    const displayScale = this.box.w / naturalW;
    const sourceSize = size / LOUPE_ZOOM / displayScale;
    const cx = point[0] * naturalW;
    const cy = point[1] * this.image.naturalHeight;

    const context = this.loupe.getContext('2d');
    context.fillStyle = '#000';
    context.fillRect(0, 0, this.loupe.width, this.loupe.height);
    context.drawImage(this.image, cx - sourceSize / 2, cy - sourceSize / 2, sourceSize, sourceSize, 0, 0, this.loupe.width, this.loupe.height);
    context.strokeStyle = 'rgba(255,255,255,.9)';
    context.lineWidth = 2;
    const mid = this.loupe.width / 2;
    context.beginPath();
    context.moveTo(mid - 14, mid); context.lineTo(mid + 14, mid);
    context.moveTo(mid, mid - 14); context.lineTo(mid, mid + 14);
    context.stroke();

    const [px, py] = this.px(point);
    const x = Math.min(this.wrap.clientWidth - size / 2, Math.max(size / 2, this.box.x + px));
    const above = this.box.y + py - size / 2 - 64;
    const y = above > 4 ? above : this.box.y + py + size / 2 + 64;
    this.loupe.style.left = `${x - size / 2}px`;
    this.loupe.style.top = `${y - size / 2}px`;
    this.loupe.hidden = false;
  }
}
