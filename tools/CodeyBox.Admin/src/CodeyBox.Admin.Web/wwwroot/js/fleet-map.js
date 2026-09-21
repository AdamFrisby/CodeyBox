// Fleet map canvas renderer (vanilla JS, no frameworks).
//
// One continuous level-of-detail progression, a pure function of apparent
// size and applied to every node alike:
//   dot (shape + glyph) → compact (+ name) → card (name, state, relations)
//   → bubble: the same card, grown large enough to render the item's stage
//   circuit — plan → work → audit ⇒ merge → landed, with audit's fail path
//   as a dotted return edge — or, for an item that needs a person, the
//   evidence and the actions to decide with. Between card and bubble the
//   card's text cross-fades into the richer content; nothing "opens".
//
// Names are the handle: the title leads every representation that has room
// for text; ids are secondary, faint, and copyable elsewhere.
//
// Cost model: requestAnimationFrame runs only while the camera is travelling
// or a recent transition is pulsing, or a layout change is gliding into
// place. A settled camera on a quiet fleet
// schedules zero frames. Items being worked on right now animate on a
// separate fx canvas that is fully cleared each frame (dirty-rect tracking
// drifted out of step and left streaks — do not bring it back).
// prefers-reduced-motion disables travel, pulses and the running animation;
// hidden tabs pause it.
//
// Colours come from the shell's CSS custom properties, read at init and on
// theme change. Every .NET interop call is a promise: rejections are caught.
(function () {
    "use strict";

    var PULSE_MS = 3200;
    var FX_FRAME_MS = 40;         // ~25 fps for the running-item animation
    var FX_PERIOD_MS = 2400;      // one revolution of the working ring
    var STAGES = ["plan", "work", "audit", "merge", "landed"];
    // Bubble geometry in card units (the card is nodeW × nodeH map units, 180 × 120).
    var B = { stageX0: 24, stagePitch: 33, stageR: 4.2, rowY: 84, tierBase: 10, tierGap: 8.5, maxTier: 2, titleLines: 3, lineH: 5.6 };
    var maps = {};

    // ── theme ────────────────────────────────────────────────────────────
    function readTheme(el) {
        var cs = getComputedStyle(el);
        function v(name, fallback) {
            var val = cs.getPropertyValue(name);
            return val && val.trim() ? val.trim() : fallback;
        }
        var t = {
            bg: v("--bg-inset", "#0a0e14"),
            card: v("--bg-card", "#161d29"),
            overlay: v("--bg-overlay", "#1f2837"),
            hover: v("--bg-hover", "#1c2432"),
            border: v("--border", "#2a3446"),
            borderStrong: v("--border-strong", "#3a4761"),
            text: v("--text", "#d9e1ef"),
            dim: v("--text-dim", "#9fabc2"),
            faint: v("--text-faint", "#6b7791"),
            accent: v("--accent", "#6ea8fe"),
            accentStrong: v("--accent-strong", "#9cc2ff"),
            danger: v("--danger", "#f07a7a"),
            ok: v("--ok", "#5fd39a"),
            warn: v("--warn", "#e8b23f"),
            info: v("--info", "#6ea8fe"),
            violet: v("--violet", "#b794f6"),
            orange: v("--orange", "#ef9a5e"),
            mono: v("--mono", "ui-monospace, Menlo, Consolas, monospace"),
            sans: "system-ui, -apple-system, 'Segoe UI', sans-serif",
            light: document.documentElement.getAttribute("data-theme") === "light"
        };
        t.tones = {
            active: t.ok, queued: t.info, review: t.violet, rework: t.orange,
            wait: t.warn, done: t.ok, fail: t.danger, muted: t.faint
        };
        return t;
    }

    function tone(st, key) { return st.theme.tones[key] || st.theme.faint; }

    function alpha(hex, a) {
        if (typeof hex === "string" && hex[0] === "#" && (hex.length === 7 || hex.length === 4)) {
            var h = hex.length === 4 ? "#" + hex[1] + hex[1] + hex[2] + hex[2] + hex[3] + hex[3] : hex;
            var r = parseInt(h.slice(1, 3), 16), g = parseInt(h.slice(3, 5), 16), b = parseInt(h.slice(5, 7), 16);
            return "rgba(" + r + "," + g + "," + b + "," + a + ")";
        }
        return hex;
    }

    // ── geometry ─────────────────────────────────────────────────────────
    function toScreen(st, wx, wy) {
        return { x: (wx - st.cam.x) * st.cam.zoom + st.w / 2, y: (wy - st.cam.y) * st.cam.zoom + st.h / 2 };
    }
    function easeInOutCubic(t) { return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; }
    function clamp(v, lo, hi) { return Math.max(lo, Math.min(hi, v)); }
    // 1 inside the canvas, falling to 0 over FADE_PX outside it.
    var FADE_PX = 180;
    function visibility(st, p) {
        var dx = p.x < 0 ? -p.x : p.x > st.w ? p.x - st.w : 0, dy = p.y < 0 ? -p.y : p.y > st.h ? p.y - st.h : 0;
        return clamp(1 - Math.max(dx, dy) / FADE_PX, 0, 1);
    }
    function smoothstep(a, b, x) { var t = clamp((x - a) / Math.max(1e-6, b - a), 0, 1); return t * t * (3 - 2 * t); }
    function openFraction(st) {
        var o = st.frame && st.frame.opts;
        return o ? smoothstep(o.openZoomStart, o.openZoomEnd, st.cam.zoom) : 0;
    }
    function detailLevel(st) {
        var o = st.frame.opts, z = st.cam.zoom;
        return z >= o.fullDetailZoom ? 2 : z >= o.compactDetailZoom ? 1 : 0;
    }
    function nodeRadius(st) { return clamp(9 * st.cam.zoom, 4, 16); }

    // ── scheduling ───────────────────────────────────────────────────────
    function pulsesActive(st, now) {
        for (var k in st.pulses) if (st.pulses[k] > now) return true;
        return false;
    }
    function kick(st) {
        if (st.raf) return;
        st.raf = requestAnimationFrame(function () { frame(st); });
    }
    function frame(st) {
        st.raf = 0;
        var now = performance.now();
        var moving = false;
        if (st.travel) {
            var tr = st.travel, t = clamp((now - tr.start) / tr.dur, 0, 1), e = easeInOutCubic(t);
            st.cam.x = tr.fx + (tr.tx - tr.fx) * e;
            st.cam.y = tr.fy + (tr.ty - tr.fy) * e;
            st.cam.zoom = Math.exp(Math.log(tr.fz) + (Math.log(tr.tz) - Math.log(tr.fz)) * e);
            if (t >= 1) { st.cam.x = tr.tx; st.cam.y = tr.ty; st.cam.zoom = tr.tz; st.travel = null; if (tr.onDone) tr.onDone(); }
            else moving = true;
        }
        if (st.tween && tweenStep(st, now)) moving = true;
        draw(st);
        if (moving || (!st.reduced && pulsesActive(st, now))) kick(st);
    }

    // ── layout tween ─────────────────────────────────────────────────────
    // When a frame moves nodes — and only the data can: a landing, a
    // forecast change, the axis bucket stepping — the boxes glide from where
    // they were to where they are, ~380ms, so the eye can follow. Zoom never
    // moves a node, so zooming never starts one. Off under reduced motion;
    // costs frames only while it runs.
    var TWEEN_MS = 380;
    function beginTween(st, prev, fr, now) {
        if (st.reduced || !prev || !fr) return;
        var from = {}, i, n;
        // From wherever a box is drawn right now — mid-glide included — so a
        // frame arriving during a tween never jumps.
        for (i = 0; i < (prev.nodes || []).length; i++) { n = prev.nodes[i]; from[n.id] = { x: n.x, y: n.y }; }
        var any = false;
        function arm(o, f) {
            if (!f || (Math.abs(f.x - o.x) < 0.5 && Math.abs(f.y - o.y) < 0.5)) return;
            o.tx = o.x; o.ty = o.y; o.fx = f.x; o.fy = f.y; o.x = f.x; o.y = f.y; any = true;
        }
        for (i = 0; i < fr.nodes.length; i++) { n = fr.nodes[i]; arm(n, from[n.id]); }
        st.tween = any ? { start: now } : null;
    }
    function tweenStep(st, now) {
        var fr = st.frame, t = clamp((now - st.tween.start) / TWEEN_MS, 0, 1), e = easeInOutCubic(t), i, o;
        var all = fr.nodes || [];
        for (i = 0; i < all.length; i++) {
            o = all[i];
            if (o.tx === undefined) continue;
            o.x = o.fx + (o.tx - o.fx) * e; o.y = o.fy + (o.ty - o.fy) * e;
            if (t >= 1) { o.x = o.tx; o.y = o.ty; delete o.tx; delete o.ty; delete o.fx; delete o.fy; }
        }
        if (t >= 1) { st.tween = null; return false; }
        return true;
    }
    // ── camera bounds: the edge of the world is discoverable ───────────
    // Mirror of CameraBounds in the model: the content's extent plus a
    // margin. A drag past it meets resistance and settles back on release;
    // a wheel or key past it is simply held. None of this touches a node.
    function contentBounds(st, zoom) {
        var fr = st.frame, o = fr && fr.opts;
        if (!fr || !o || !fr.nodes.length) return null;
        var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity, i, n;
        for (i = 0; i < fr.nodes.length; i++) { n = fr.nodes[i]; var x = n.tx !== undefined ? n.tx : n.x, y = n.ty !== undefined ? n.ty : n.y; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        var z = zoom > 0 ? zoom : 1;
        var mx = Math.max(o.nodeW, st.w * o.panMargin / z), my = Math.max(o.nodeH, st.h * o.panMargin / z);
        return { minX: minX - o.nodeW / 2 - mx, minY: minY - o.nodeH / 2 - my, maxX: maxX + o.nodeW / 2 + mx, maxY: maxY + o.nodeH / 2 + my };
    }
    function minZoom(st) {
        var o = st.frame && st.frame.opts, b = contentBounds(st, 1);
        if (!o || !b) return 0.08;
        var fit = Math.min(st.w / Math.max(1, b.maxX - b.minX), st.h / Math.max(1, b.maxY - b.minY));
        return Math.max(o.minFitZoom * 0.5, Math.min(fit, o.minFitZoom * 8));
    }
    function clampCam(cam, st) {
        var z = Math.max(cam.zoom, minZoom(st)), b = contentBounds(st, z);
        if (!b) return { x: cam.x, y: cam.y, zoom: z };
        var hw = st.w / 2 / z, hh = st.h / 2 / z;
        var x = hw * 2 >= b.maxX - b.minX ? (b.minX + b.maxX) / 2 : clamp(cam.x, b.minX + hw, b.maxX - hw);
        var y = hh * 2 >= b.maxY - b.minY ? (b.minY + b.maxY) / 2 : clamp(cam.y, b.minY + hh, b.maxY - hh);
        return { x: x, y: y, zoom: z };
    }
    // Rubber band: past the bound the view follows the hand at a third of the pace.
    function softClamp(raw, st) {
        var c = clampCam(raw, st);
        return { x: c.x + (raw.x - c.x) * 0.35, y: c.y + (raw.y - c.y) * 0.35, zoom: c.zoom };
    }
    function setTarget(st, cx, cy, zoom, ms) {
        if (!isFinite(cx) || !isFinite(cy) || !isFinite(zoom) || zoom <= 0) return;
        var same = Math.abs(cx - st.cam.x) < 0.01 && Math.abs(cy - st.cam.y) < 0.01 && Math.abs(zoom - st.cam.zoom) < 1e-4;
        if (same) { st.travel = null; return; }
        if (st.reduced || !ms || ms <= 0) {
            st.travel = null;
            st.cam.x = cx; st.cam.y = cy; st.cam.zoom = zoom;
            return;
        }
        st.travel = { fx: st.cam.x, fy: st.cam.y, fz: st.cam.zoom, tx: cx, ty: cy, tz: zoom, start: performance.now(), dur: ms };
        kick(st);
    }

    // ── primitives ───────────────────────────────────────────────────────
    function roundRect(ctx, x, y, w, h, r) {
        r = Math.min(r, w / 2, h / 2);
        ctx.beginPath();
        ctx.moveTo(x + r, y);
        ctx.arcTo(x + w, y, x + w, y + h, r);
        ctx.arcTo(x + w, y + h, x, y + h, r);
        ctx.arcTo(x, y + h, x, y, r);
        ctx.arcTo(x, y, x + w, y, r);
        ctx.closePath();
    }
    function drawShape(ctx, shape, x, y, r) {
        ctx.beginPath();
        if (shape === "square") {
            ctx.rect(x - r * 0.9, y - r * 0.9, r * 1.8, r * 1.8);
        } else if (shape === "diamond") {
            ctx.moveTo(x, y - r * 1.15); ctx.lineTo(x + r * 1.15, y);
            ctx.lineTo(x, y + r * 1.15); ctx.lineTo(x - r * 1.15, y); ctx.closePath();
        } else if (shape === "triangle") {
            ctx.moveTo(x, y - r * 1.2); ctx.lineTo(x + r * 1.15, y + r * 0.9);
            ctx.lineTo(x - r * 1.15, y + r * 0.9); ctx.closePath();
        } else if (shape === "hexagon") {
            for (var i = 0; i < 6; i++) {
                var a = Math.PI / 3 * i - Math.PI / 6;
                var px = x + r * Math.cos(a), py = y + r * Math.sin(a);
                if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
            }
            ctx.closePath();
        } else {
            ctx.arc(x, y, r, 0, Math.PI * 2);
        }
    }
    function fitText(ctx, text, maxW) {
        if (!text) return "";
        if (ctx.measureText(text).width <= maxW) return text;
        var lo = 0, hi = text.length;
        while (lo < hi) {
            var mid = (lo + hi + 1) >> 1;
            if (ctx.measureText(text.slice(0, mid) + "…").width <= maxW) lo = mid; else hi = mid - 1;
        }
        return lo <= 0 ? "" : text.slice(0, lo) + "…";
    }
    // Word-wrap into at most maxLines; the last line is ellipsised only if
    // the text genuinely does not fit. Never breaks inside a word unless a
    // single word is wider than the line.
    // ── overlap discipline ───────────────────────────────────────────────
    // Every overlap on this canvas is a decision. Bodies (nodes, clusters,
    // ghosts, HUD) claim their screen rectangles first; every label then
    // asks the registry for a free spot among its candidates and, when none
    // is free, is drawn on a plate so it sits over content on purpose. Lines
    // that must pass beneath a body are broken by the body's halo — a
    // background-coloured rim every body draws before itself — so a crossing
    // reads as "under", never as "ended here".
    function Occupancy() { this.rects = []; }
    Occupancy.prototype.claim = function (x, y, w, h, kind) { this.rects.push({ x: x, y: y, w: w, h: h, kind: kind || "body" }); };
    Occupancy.prototype.free = function (x, y, w, h, kinds) {
        for (var i = 0; i < this.rects.length; i++) {
            var r = this.rects[i];
            if (kinds && kinds.indexOf(r.kind) < 0) continue;
            if (x < r.x + r.w && x + w > r.x && y < r.y + r.h && y + h > r.y) return false;
        }
        return true;
    };
    // The first candidate that is free, else the first candidate, plated.
    Occupancy.prototype.place = function (cands, w, h, kinds) {
        for (var i = 0; i < cands.length; i++) {
            if (this.free(cands[i].x, cands[i].y, w, h, kinds)) { this.claim(cands[i].x, cands[i].y, w, h, "label"); return { x: cands[i].x, y: cands[i].y, plated: false }; }
        }
        this.claim(cands[0].x, cands[0].y, w, h, "label");
        return { x: cands[0].x, y: cands[0].y, plated: true };
    };
    // Free width to the right of x inside the band [y, y+h) before the next body.
    Occupancy.prototype.roomRight = function (x, y, h, maxW) {
        var room = maxW;
        for (var i = 0; i < this.rects.length; i++) {
            var r = this.rects[i];
            if (r.kind !== "body" || r.x <= x || y >= r.y + r.h || y + h <= r.y) continue;
            room = Math.min(room, r.x - x - 4);
        }
        return room;
    };
    // A label: measured, placed among candidates (top-left corners), plated when it must sit over content.
    function label(st, text, font, color, cands, opts) {
        var ctx = st.ctx, th = st.theme, o = opts || {};
        if (!text) return null;
        ctx.font = font;
        var w = ctx.measureText(text).width, h = o.h || 12;
        var at = st.occ.place(cands.map(function (c) { return { x: c.x - (o.align === "center" ? w / 2 : o.align === "right" ? w : 0), y: c.y }; }), w + 4, h, o.kinds);
        if (at.plated && o.drop) { return null; }
        if (at.plated || o.plate) {
            roundRect(ctx, at.x - 3, at.y - 1, w + 6, h + 2, 3);
            ctx.fillStyle = alpha(th.bg, o.plateAlpha || 0.82); ctx.fill();
        }
        ctx.font = font; ctx.fillStyle = color; ctx.textAlign = "left"; ctx.textBaseline = "top";
        ctx.fillText(text, at.x, at.y);
        return { x: at.x, y: at.y, w: w, h: h, plated: at.plated };
    }
    // The halo: a background rim under a body so anything passing beneath breaks cleanly.
    function halo(st, width) {
        var ctx = st.ctx;
        ctx.save(); ctx.setLineDash([]); ctx.globalAlpha = 1;
        ctx.lineWidth = width || 4; ctx.strokeStyle = st.theme.bg; ctx.stroke();
        ctx.restore();
    }
    function wrapWords(ctx, text, maxW, maxLines) {
        var words = (text || "").split(/\s+/).filter(Boolean), lines = [], line = "";
        for (var i = 0; i < words.length; i++) {
            var candidate = line ? line + " " + words[i] : words[i];
            if (ctx.measureText(candidate).width <= maxW || !line) {
                line = candidate;
            } else {
                lines.push(line);
                line = words[i];
                if (lines.length === maxLines) break;
            }
        }
        if (lines.length < maxLines && line) lines.push(line);
        if (lines.length === maxLines) {
            var rest = words.slice(lines.join(" ").split(/\s+/).length).join(" ");
            if (rest || ctx.measureText(lines[maxLines - 1]).width > maxW) lines[maxLines - 1] = fitText(ctx, lines[maxLines - 1] + (rest ? " " + rest : ""), maxW);
        } else if (lines.length && ctx.measureText(lines[lines.length - 1]).width > maxW) {
            lines[lines.length - 1] = fitText(ctx, lines[lines.length - 1], maxW);
        }
        return lines;
    }

    // ── scene ────────────────────────────────────────────────────────────
    function draw(st) {
        var ctx = st.ctx, th = st.theme;
        if (!ctx) return;
        ctx.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
        ctx.clearRect(0, 0, st.w, st.h);
        var fr = st.frame;
        st.hit = [];
        st.running = [];
        if (!fr) { fxSync(st); return; }

        var now = performance.now();
        var z = st.cam.zoom, level = detailLevel(st), o = fr.opts, open = openFraction(st);
        var byId = {}, i, n;
        for (i = 0; i < fr.nodes.length; i++) byId[fr.nodes[i].id] = fr.nodes[i];
        var r = nodeRadius(st);
        var halfW = o.nodeW * z / 2, halfH = o.nodeH * z / 2;

        // Bodies claim first: nodes, clusters, ghosts, and the HUD; labels ask later.
        var occ = st.occ = new Occupancy();
        occ.claim(0, 0, st.w, 24, "hud");
        if (st.legendRect) occ.claim(st.legendRect.x, st.legendRect.y, st.legendRect.w, st.legendRect.h, "hud");
        function bodyRect(wx, wy) {
            var q = toScreen(st, wx, wy);
            return level === 2 ? { x: q.x - halfW, y: q.y - halfH, w: halfW * 2, h: halfH * 2 } : { x: q.x - r - 2, y: q.y - r - 2, w: r * 2 + 4, h: r * 2 + 4 };
        }
        // Draw-time clusters: landed items of one row that sit too close on
        // screen to draw apart at this zoom are drawn as one counted glyph at
        // their centroid. Their world positions never change — zooming in
        // simply stops drawing the glyph and starts drawing the members that
        // were always there, the same rule as a dot becoming a card.
        var groups = groupClose(st, fr, level, r), hidden = {};
        for (i = 0; i < groups.length; i++) for (var gm = 0; gm < groups[i].members.length; gm++) hidden[groups[i].members[gm].id] = groups[i];
        for (i = 0; i < fr.nodes.length; i++) { n = fr.nodes[i]; if (hidden[n.id]) continue; var br0 = bodyRect(n.x, n.y); if (br0.x < st.w && br0.x + br0.w > 0 && br0.y < st.h && br0.y + br0.h > 0) occ.claim(br0.x, br0.y, br0.w, br0.h, "body"); }
        for (i = 0; i < groups.length; i++) { var cr0 = bodyRect(groups[i].x, groups[i].y); occ.claim(cr0.x - 4, cr0.y - 6, cr0.w + 8, cr0.h + 6, "body"); }
        for (i = 0; i < (fr.ghosts || []).length; i++) { var gr0 = bodyRect(fr.ghosts[i].x, fr.ghosts[i].y); occ.claim(gr0.x, gr0.y, gr0.w, gr0.h, "body"); }

        // The axis: the future half is tinted so predictions read as a
        // different kind of thing from the observed past, and "now" is a
        // permanent line down the whole canvas.
        var nowSx = toScreen(st, fr.axis ? fr.axis.nowX : 0, 0).x;
        if (nowSx < st.w) {
            ctx.fillStyle = alpha(th.accent, th.light ? 0.035 : 0.045);
            ctx.fillRect(Math.max(0, nowSx), 0, st.w - Math.max(0, nowSx), st.h);
        }
        ctx.strokeStyle = alpha(th.accent, 0.55); ctx.lineWidth = 1.2; ctx.setLineDash([]);
        ctx.beginPath(); ctx.moveTo(nowSx, 0); ctx.lineTo(nowSx, st.h); ctx.stroke();

        // Quiet cut from the axis: a narrow, low-contrast break the full
        // height of the canvas, so spacing across it is never read as
        // elapsed time without a hint; hover or the ruler readout say more.
        var breaks = fr.axis && fr.axis.breaks ? fr.axis.breaks : [];
        for (i = 0; i < breaks.length; i++) {
            var bk = breaks[i], bsx = toScreen(st, bk.x, 0).x, bsw = Math.max(6, bk.w * z);
            if (bsx > st.w || bsx + bsw < 0) continue;
            // Discreet: a slightly darker band with dotted edges. Noticeable
            // when looked for; the ruler readout and hover say what it skipped.
            var hotBreak = st.hover === "break:" + i;
            ctx.fillStyle = alpha(th.bg, hotBreak ? 0.5 : 0.28); ctx.fillRect(bsx, 0, bsw, st.h);
            ctx.strokeStyle = alpha(th.faint, hotBreak ? 0.55 : 0.22); ctx.lineWidth = 1; ctx.setLineDash([2, 5]);
            ctx.beginPath(); ctx.moveTo(bsx + 0.5, 0); ctx.lineTo(bsx + 0.5, st.h); ctx.moveTo(bsx + bsw - 0.5, 0); ctx.lineTo(bsx + bsw - 0.5, st.h); ctx.stroke();
            ctx.setLineDash([]);
            st.hit.push({ id: "break:" + i, brk: bk, x: bsx, y: 0, w: bsw, h: st.h });
        }

        // Releases: the tier above lanes. At overview a tint and a name; the
        // frame itself only once there is room for it to read as a container.
        var frameT = smoothstep(o.compactDetailZoom * 0.6, o.compactDetailZoom * 1.6, z);
        for (i = 0; i < (fr.releases || []).length; i++) {
            var rl = fr.releases[i];
            var rp = toScreen(st, rl.x, rl.y), rw = rl.w * z, rh = rl.h * z;
            if (rp.x > st.w || rp.y > st.h || rp.x + rw < 0 || rp.y + rh < 0) continue;
            roundRect(ctx, rp.x, rp.y, rw, rh, 12);
            ctx.fillStyle = alpha(th.violet, th.light ? 0.07 : 0.06); ctx.fill();
            if (frameT > 0) {
                ctx.lineWidth = 1; ctx.strokeStyle = alpha(th.violet, 0.35 * frameT); ctx.setLineDash([]); ctx.stroke();
            }
            ctx.font = "700 11px " + th.sans;
            var rlabel = fitText(ctx, "◫ " + rl.name, Math.max(60, rw - 12));
            var rlAt = label(st, rlabel, "700 11px " + th.sans, alpha(th.violet, 0.95), [{ x: rp.x + 8, y: rp.y + 5 }, { x: rp.x + 8, y: rp.y - 16 }], { h: 13 });
            var rmeta = rl.state + " · " + rl.done + "/" + rl.total + " landed" + (rl.blocking ? " · " + rl.blocking + " blocking" : "");
            if (rlAt) label(st, rmeta, "10px " + th.mono, alpha(th.dim, 0.9), [{ x: rlAt.x + rlAt.w + 8, y: rlAt.y + 1 }], { h: 12, drop: true });
            // Release-level findings that spawned remediation work: a dotted relation from the header.
            for (var ri = 0; ri < (rl.remediation || []).length; ri++) {
                var rn = byId[rl.remediation[ri]];
                if (!rn) continue;
                var rq = toScreen(st, rn.x, rn.y);
                ctx.strokeStyle = alpha(th.violet, 0.6); ctx.lineWidth = 1.1; ctx.setLineDash([2, 4]);
                ctx.beginPath(); ctx.moveTo(rp.x + 8, rp.y + 18); ctx.lineTo(rq.x, rq.y); ctx.stroke(); ctx.setLineDash([]);
                if (z >= o.compactDetailZoom) {
                    ctx.font = "600 10px " + th.mono; ctx.fillStyle = alpha(th.violet, 0.9); ctx.textBaseline = "bottom";
                    ctx.fillText("remediation", (rp.x + 8 + rq.x) / 2, (rp.y + 18 + rq.y) / 2 - 2);
                    ctx.textBaseline = "top";
                }
            }
        }

        // Lanes: a faint frame per chain with its name — the shape of the backlog.
        ctx.font = "600 11px " + th.sans;
        ctx.textBaseline = "bottom"; ctx.textAlign = "left";
        for (i = 0; i < (fr.lanes || []).length; i++) {
            var ln = fr.lanes[i];
            var p0 = toScreen(st, ln.x, ln.y), sw = ln.w * z, sh = ln.h * z;
            if (p0.x > st.w || p0.y > st.h || p0.x + sw < 0 || p0.y + sh < 0) continue;
            roundRect(ctx, p0.x, p0.y, sw, sh, 8);
            ctx.fillStyle = alpha(th.card, th.light ? 0.55 : 0.35);
            ctx.fill();
            ctx.strokeStyle = alpha(th.border, 0.9); ctx.lineWidth = 1; ctx.stroke();
            if (sw > 48) {
                // The name sits in the gutter above the lane, stays in view when
                // the lane runs off the left edge, and is plated when the gutter
                // is too thin for it at this zoom: the chain's name is essential.
                // Pinned: a visible lane's name never rides off an edge or under the legend.
                var legendTop = st.legendRect ? st.legendRect.y - 4 : st.h, lyTop = 26, lyBot = Math.max(lyTop, legendTop - 14);
                var lx0 = clamp(p0.x + 6, 4, Math.max(4, st.w - 64)), room = Math.min(p0.x + sw, st.w) - lx0 - 4;
                ctx.font = "600 11px " + th.sans;
                var lbl = fitText(ctx, ln.label, Math.max(40, room));
                var topY = clamp(p0.y - 16, lyTop, lyBot), botY = clamp(p0.y + sh - 14, lyTop, lyBot);
                var lAt = label(st, lbl, "600 11px " + th.sans, alpha(th.dim, 0.95),
                    [{ x: lx0, y: topY }, { x: lx0, y: clamp(topY + 15, lyTop, lyBot) }, { x: lx0, y: clamp(topY + 30, lyTop, lyBot) }, { x: lx0, y: botY }, { x: lx0, y: clamp(botY - 15, lyTop, lyBot) }],
                    { h: 13, kinds: ["body", "label", "stub"] });
                if (lAt && (ln.n > 1 || ln.blocked || ln.running || ln.settled)) {
                    var meta = ln.n + (ln.n === 1 ? " item" : " items") + (ln.blocked ? " · " + ln.blocked + " blocked" : "") + (ln.running ? " · " + ln.running + " running" : "") + (ln.settled ? " · " + ln.settled + " settled" : "");
                    if (lAt.x + lAt.w + 8 + ctx.measureText(meta).width < p0.x + sw) label(st, meta, "10px " + th.mono, alpha(th.faint, 0.95), [{ x: lAt.x + lAt.w + 8, y: lAt.y + 1 }], { h: 12, drop: true, kinds: ["body", "label"] });
                }
            }
        }

        // Edges: dependency → dependent. Fans from one source (or into one
        // target) run as a bundle of parallel lines — subway-track style — to
        // a split point and fan out from there; every line stays its own
        // line. A source off-screen is named at the point its wire enters,
        // and the wire starts at that name. Edges with neither end in view
        // recede; an edge with a named source does not.
        drawEdges(st, byId, level, halfW, halfH, r);

        // The "+ dependent" preview for the hovered node: an empty box drawn
        // where the new node would land, joined by the edge that would exist.

        // Suggestions as ghosts: provisional boxes, pre-filled, where promotion would put them.
        for (i = 0; i < (fr.ghosts || []).length; i++) drawSuggestionGhost(st, fr.ghosts[i], byId, level, halfW, halfH, r, open);

        // Nodes, one representation per zoom, for every node alike.
        for (i = 0; i < fr.nodes.length; i++) {
            n = fr.nodes[i];
            if (hidden[n.id]) continue; // drawn as part of a close group at this zoom
            var p = toScreen(st, n.x, n.y);
            if (p.x < -halfW - 200 || p.x > st.w + halfW + 200 || p.y < -halfH - 80 || p.y > st.h + halfH + 80) continue;
            var color = n.settled ? th.faint : tone(st, n.tone);
            var hovered = st.hover === n.id;
            var isRunning = n.activity === "Running";
            // Settled work is dim history; predicted positions are drawn a little
            // less committed than observed ones — a forecast, not a fact.
            ctx.globalAlpha = n.settled ? 0.6 : n.zone === "future" ? 0.88 : 1;

            if (level === 2) {
                drawCard(st, n, p, color, halfW, halfH, hovered, open, fr.details && fr.details[n.id]);
                st.hit.push({ id: n.id, x: p.x - halfW, y: p.y - halfH, w: halfW * 2, h: halfH * 2 });
                if (canTakeDependent(n)) drawPlus(st, n, p, level, halfW, r);
                if (isRunning) st.running.push({ id: n.id, x: p.x - halfW + 9 * z, y: p.y - halfH + 8 * z, r: Math.max(4, 3 * z), color: color, ring: false });
            } else {
                drawShape(ctx, n.shape, p.x, p.y, r);
                halo(st, 5);
                ctx.fillStyle = n.settled ? alpha(th.faint, 0.28) : th.bg; ctx.fill();
                ctx.lineWidth = n.urgency || hovered ? 2.4 : 1.6;
                if (n.settled) ctx.setLineDash([3, 2]);
                ctx.strokeStyle = color; ctx.stroke(); ctx.setLineDash([]);
                if (level === 1 || r >= 8) {
                    ctx.fillStyle = color;
                    ctx.font = Math.max(9, r * 1.1) + "px " + th.sans;
                    ctx.textAlign = "center"; ctx.textBaseline = "middle";
                    ctx.fillText(n.glyph || "•", p.x, p.y + 0.5);
                }
                var labelW = 0;
                if (level === 1) {
                    // The name is the handle, even here: a truncated name beats a
                    // hex string. It takes the room to the next body on its row,
                    // and moves below the dot when there is none.
                    var nf = Math.max(o.minTextPx, 10) + "px " + th.sans;
                    ctx.font = nf;
                    var roomR = occ.roomRight(p.x + r + 5, p.y - 6, 12, Math.max(60, (o.colGap * z) - r * 2 - 14));
                    var nm = fitText(ctx, n.title || "", roomR >= 40 ? roomR : Math.max(60, (o.colGap * z) - r * 2 - 14));
                    var nAt = label(st, nm, nf, alpha(n.settled ? th.faint : th.dim, 0.95),
                        roomR >= 40 ? [{ x: p.x + r + 5, y: p.y - 6 }] : [{ x: p.x - r, y: p.y + r + 3 }, { x: p.x - r, y: p.y - r - 15 }, { x: p.x + r + 5, y: p.y - 6 }], { h: 12 });
                    if (nAt) labelW = nAt.x >= p.x ? nAt.w + 6 : 0;
                }
                st.hit.push({ id: n.id, x: p.x - r - 2, y: p.y - r - 2, w: r * 2 + 4 + labelW, h: r * 2 + 4 });
                if (level === 1 && hovered && canTakeDependent(n)) drawPlus(st, n, { x: p.x + labelW, y: p.y }, level, halfW, r);
                if (isRunning) st.running.push({ id: n.id, x: p.x, y: p.y, r: r + 4, color: color, ring: true });
                if (n.waiting >= 3) {
                    label(st, "⇢ " + n.waiting + " waiting", "600 " + Math.max(o.minTextPx, 10) + "px " + th.mono, alpha(th.orange, 0.95),
                        [{ x: p.x, y: p.y + r + 4 }, { x: p.x, y: p.y - r - 15 }, { x: p.x + r + 6, y: p.y + r + 2 }, { x: p.x, y: p.y + r + 18 }, { x: p.x + r + 6, y: p.y - r - 15 }], { h: 12, align: "center", kinds: ["body", "label", "stub", "hud"] });
                }
            }
            ctx.globalAlpha = 1;

            if (n.pushed && level >= 1) {
                // Placed right of a blocker rather than at its own time: say so.
                label(st, "⇤ after its blocker", "600 " + Math.max(o.minTextPx, 10) + "px " + th.mono, alpha(th.warn, 0.9),
                    level === 2 ? [{ x: p.x - halfW + 6, y: p.y - halfH - 15 }, { x: p.x - halfW + 6, y: p.y + halfH + 3 }] : [{ x: p.x + r + 5, y: p.y - r - 15 }, { x: p.x + r + 5, y: p.y + r + 3 }], { h: 12 });
            }
            if (n.urgency || n.needsYou) {
                ctx.beginPath();
                if (level === 2) roundRect(ctx, p.x - halfW - 4, p.y - halfH - 4, halfW * 2 + 8, halfH * 2 + 8, 8);
                else ctx.arc(p.x, p.y, r + 5, 0, Math.PI * 2);
                ctx.lineWidth = 1.2; ctx.strokeStyle = color; ctx.stroke();
            }
            if (n.tx !== undefined && st.tween) {
                // Mid-glide after a deliberate re-layout (a band change split or
                // folded the past): a fading ring says "this one moved on purpose".
                var gt = clamp((now - st.tween.start) / TWEEN_MS, 0, 1);
                ctx.beginPath();
                if (level === 2) roundRect(ctx, p.x - halfW - 3, p.y - halfH - 3, halfW * 2 + 6, halfH * 2 + 6, 7); else ctx.arc(p.x, p.y, r + 4, 0, Math.PI * 2);
                ctx.setLineDash([3, 3]); ctx.lineWidth = 1.5; ctx.strokeStyle = alpha(th.accent, 0.9 * (1 - gt)); ctx.stroke(); ctx.setLineDash([]);
            }
            var until = st.pulses[n.id] || 0;
            if (until > now && !st.reduced) {
                var tt = 1 - (until - now) / PULSE_MS;
                ctx.beginPath(); ctx.arc(p.x, p.y, (level === 2 ? halfH : r) + 5 + tt * 28, 0, Math.PI * 2);
                ctx.lineWidth = 2; ctx.strokeStyle = color;
                ctx.globalAlpha = 0.8 * (1 - tt); ctx.stroke(); ctx.globalAlpha = 1;
            }
        }

        // Close groups, drawn where their members are.
        var byCluster = {};
        for (i = 0; i < groups.length; i++) {
            var cl = groups[i];
            byCluster[cl.id] = cl;
            drawCluster(st, cl, toScreen(st, cl.x, cl.y), r, st.hover === cl.id);
        }
        st.byCluster = byCluster;

        // Rail leaders: the topmost annotation layer, routed through the
        // gutters between lanes and drawn with a halo, so they never run
        // through a node and every crossing reads as "over".
        drawLeaders(st, byId, level, halfW, halfH, r);

        // Camera focus ring (static): the item the camera centred on.
        if (fr.camera && fr.camera.open && byId[fr.camera.open] && !fr.camera.manual) {
            var f = byId[fr.camera.open], fp = toScreen(st, f.x, f.y);
            ctx.beginPath();
            if (level === 2) roundRect(ctx, fp.x - halfW - 9, fp.y - halfH - 9, halfW * 2 + 18, halfH * 2 + 18, 10);
            else ctx.arc(fp.x, fp.y, r + 11, 0, Math.PI * 2);
            ctx.setLineDash([5, 4]); ctx.lineWidth = 1.4; ctx.strokeStyle = alpha(th.text, 0.6); ctx.stroke(); ctx.setLineDash([]);
        }

        if (st.hover && byId[st.hover] && level < 2 && !st.hoverGhost) drawTooltip(st, byId[st.hover]);
        if (st.hoverGhost && byId[st.hoverGhost] && st.pointer) {
            var gp = byId[st.hoverGhost];
            drawTooltip(st, { id: gp.id, x: (st.pointer.x - st.w / 2) / z + st.cam.x, y: (st.pointer.y - st.h / 2) / z + st.cam.y,
                title: "Add a dependent of “" + (gp.title || "") + "”",
                stateText: "creates a new item that waits on this one", sub: "placed by the forecast once it exists" });
        }
        if (st.hover && byCluster[st.hover] && level < 2) drawTooltip(st, clusterAsNode(byCluster[st.hover]));
        if (st.hover && st.hover.indexOf("break:") === 0 && breaks[+st.hover.slice(6)]) {
            var hb = breaks[+st.hover.slice(6)];
            drawTooltip(st, { id: st.hover, x: hb.x + hb.w / 2, y: (st.cam.y), title: hb.exact + " of quiet cut from the axis", stateText: "nothing landed in that stretch", sub: "" });
        }
        drawRuler(st);
        fxSync(st);
    }

    // The ruler: the compression made legible. Past ticks at their log
    // positions, "now" named, future columns numbered as batches — and a
    // one-line basis so a forecast never passes for a fact.
    function drawRuler(st) {
        var fr = st.frame, ctx = st.ctx, th = st.theme;
        if (!fr.axis) return;
        var h = 22;
        ctx.fillStyle = alpha(th.card, th.light ? 0.92 : 0.88);
        ctx.fillRect(0, 0, st.w, h);
        ctx.strokeStyle = alpha(th.border, 0.9); ctx.lineWidth = 1;
        ctx.beginPath(); ctx.moveTo(0, h + 0.5); ctx.lineTo(st.w, h + 0.5); ctx.stroke();
        ctx.textBaseline = "middle";
        // "now" is drawn first and owns its space; other labels yield to it and to each other.
        var nowSx = toScreen(st, fr.axis.nowX, 0).x, nowHalf = 0;
        if (nowSx >= 0 && nowSx <= st.w) {
            ctx.font = "700 11px " + th.sans; nowHalf = ctx.measureText("now").width / 2 + 6;
            ctx.strokeStyle = alpha(th.accent, 0.9); ctx.beginPath(); ctx.moveTo(nowSx, h - 6); ctx.lineTo(nowSx, h); ctx.stroke();
            ctx.fillStyle = th.accentStrong; ctx.textAlign = "center"; ctx.fillText("now", nowSx, h / 2 - 1);
        }
        // The past, grouped for legibility: runs are walked newest first and
        // merged until a group is wide enough on screen to carry a date, so
        // every surviving group is dated and the marks between groups are
        // bounded by what fits — zooming in splits groups and dates more.
        // Positions never change; only what the ruler says about them does.
        var taken = nowHalf ? [[nowSx - nowHalf, nowSx + nowHalf]] : [], i, w, q;
        var breaks = fr.axis.breaks || [];
        function free(x0, x1) { for (var k = 0; k < taken.length; k++) if (x1 > taken[k][0] && x0 < taken[k][1]) return false; return true; }
        var LABEL_PX = 74, z0 = st.cam.zoom;
        var runs = (fr.axis.stretches || []).slice().sort(function (r1, r2) { return r2.x1 - r1.x1; });
        var groups = [], cur = null;
        for (i = 0; i < runs.length; i++) {
            var rn = runs[i];
            if (!cur) cur = { x0: rn.x0, x1: rn.x1, t0: rn.t0, t1: rn.t1, runs: 1 };
            else { cur.x0 = rn.x0; cur.t0 = rn.t0; cur.runs++; }
            if ((cur.x1 - cur.x0) * z0 >= LABEL_PX) { groups.push(cur); cur = null; }
        }
        if (cur) groups.push(cur);
        var insideGroup = function (bk) { for (var g = 0; g < groups.length; g++) if (bk.x > groups[g].x0 && bk.x + bk.w < groups[g].x1) return true; return false; };
        for (i = 0; i < groups.length; i++) {
            var g1 = groups[i], gx0 = toScreen(st, g1.x0, 0).x, gx1 = toScreen(st, g1.x1, 0).x;
            if (gx1 < 0 || gx0 > st.w) continue;
            var d0 = new Date(g1.t0), d1 = new Date(g1.t1), spanH = (d1 - d0) / 3600000;
            ctx.font = "10px " + th.mono;
            var text = spanH < 6 && g1.runs === 1 ? fmtClock(g1.t1) : fmtDay(d1);
            if (spanH >= 24 && fmtDay(d0) !== fmtDay(d1)) { var range = fmtDay(d0) + " – " + fmtDay(d1); if (ctx.measureText(range).width + 8 <= gx1 - gx0) text = range; }
            w = ctx.measureText(text).width;
            var lx = clamp((gx0 + gx1) / 2 - w / 2, Math.max(2, gx0), Math.min(st.w - w - 2, gx1 - w));
            if (lx < 2 || lx + w > st.w - 2 || !free(lx - 4, lx + w + 4)) continue;
            ctx.strokeStyle = alpha(th.faint, 0.6); ctx.beginPath(); ctx.moveTo(gx1, h - 6); ctx.lineTo(gx1, h); ctx.stroke();
            ctx.fillStyle = alpha(th.dim, 0.9); ctx.textAlign = "left"; ctx.fillText(text, lx, h / 2 - 1);
            taken.push([lx - 4, lx + w + 4]);
        }
        for (i = 0; i < breaks.length; i++) {
            var bk = breaks[i], bx0 = toScreen(st, bk.x, 0).x, bw0 = Math.max(6, bk.w * st.cam.zoom), bcx = bx0 + bw0 / 2;
            if (bx0 > st.w || bx0 + bw0 < 0) continue;
            if (insideGroup(bk) && st.hover !== "break:" + i) continue; // merged into its group's dead air
            ctx.fillStyle = alpha(th.faint, 0.1); ctx.fillRect(bx0, 0, bw0, h);
            ctx.font = "700 10px " + th.mono;
            var bl = st.hover === "break:" + i ? "⋯" + bk.label + "⋯" : "⋯";
            w = ctx.measureText(bl).width;
            var bxl = clamp(bcx - w / 2, 2, st.w - w - 2);
            if (!free(bxl - 4, bxl + w + 4)) {
                // Slide off whatever it hit (usually "now"), staying near its band; else the bare mark.
                var leftTry = clamp(bx0 + bw0 - w - 2, 2, st.w - w - 2), rightTry = clamp(bx0 + 2, 2, st.w - w - 2);
                if (bcx < nowSx && free(leftTry - 4, leftTry + w + 4)) bxl = leftTry;
                else if (free(rightTry - 4, rightTry + w + 4)) bxl = rightTry;
                else {
                    bl = "⋯"; w = ctx.measureText(bl).width; bxl = clamp(bcx - w / 2, 2, st.w - w - 2);
                    if (!free(bxl - 2, bxl + w + 2)) continue;
                }
            }
            ctx.fillStyle = alpha(th.dim, bl.length > 1 ? 0.95 : 0.5); ctx.textAlign = "left"; ctx.fillText(bl, bxl, h / 2 - 1);
            taken.push([bxl - 4, bxl + w + 4]);
        }
        var lastRight = -1e9;
        for (i = 0; i < fr.axis.ticks.length; i++) {
            var t = fr.axis.ticks[i], sx = toScreen(st, t.x, 0).x;
            if (t.zone === "now" || sx < 0 || sx > st.w) continue;
            var future = t.zone === "future";
            if (!future) continue; // the past is dated by its groups above
            ctx.font = "10px " + th.sans;
            var label = t.label === "next" ? "next" : "batch " + t.label;
            w = ctx.measureText(label).width;
            if (sx - w / 2 < lastRight + 6) continue; // crowded: skip, never overlap
            if (Math.abs(sx - nowSx) < nowHalf + w / 2) continue; // never over "now"
            if (!free(sx - w / 2, sx + w / 2)) continue; // never over a break label
            ctx.strokeStyle = alpha(th.faint, 0.6);
            ctx.beginPath(); ctx.moveTo(sx, h - 6); ctx.lineTo(sx, h); ctx.stroke();
            ctx.fillStyle = alpha(th.dim, 0.9);
            ctx.textAlign = "center";
            ctx.fillText(label, sx, h / 2 - 1);
            lastRight = sx + w / 2;
        }
        // The pointer readout: what this x means, on the ruler, at the pointer.
        if (st.pointer && st.pointer.x >= 0 && st.pointer.x <= st.w) {
            var wx = (st.pointer.x - st.w / 2) / st.cam.zoom + st.cam.x;
            var reading = readAxis(fr, wx), text;
            if (reading.zone === "now") text = "now";
            else if (reading.zone === "future") text = "batch " + (reading.batch === 0 ? "next" : "+" + reading.batch) + " · predicted order";
            else if (reading.brk) text = "in a cut · " + reading.brk.exact + " skipped, nothing landed";
            else if (reading.at) text = fmtClock(reading.at) + " · " + reading.ago;
            else text = "past";
            ctx.font = "600 10px " + th.mono;
            var rw = ctx.measureText(text).width + 10, rx = clamp(st.pointer.x - rw / 2, 2, st.w - rw - 2);
            ctx.strokeStyle = alpha(th.text, 0.5); ctx.lineWidth = 1;
            ctx.beginPath(); ctx.moveTo(st.pointer.x + 0.5, 0); ctx.lineTo(st.pointer.x + 0.5, h); ctx.stroke();
            roundRect(ctx, rx, 2, rw, h - 4, 4);
            ctx.fillStyle = alpha(th.overlay, 0.97); ctx.fill(); ctx.strokeStyle = th.borderStrong; ctx.stroke();
            ctx.fillStyle = th.text; ctx.textAlign = "left"; ctx.fillText(text, rx + 5, h / 2 - 1);
        }
        // The basis, as a quiet pill at the bottom right where nothing else lives.
        ctx.font = "10px " + th.sans;
        var basis = fitText(ctx, fr.axis.basis || "", st.w * 0.5);
        if (basis) {
            var bw = ctx.measureText(basis).width + 14;
            roundRect(ctx, st.w - bw - 8, st.h - 22, bw, 16, 8);
            ctx.fillStyle = alpha(th.card, 0.85); ctx.fill(); ctx.strokeStyle = alpha(th.border, 0.9); ctx.lineWidth = 1; ctx.stroke();
            ctx.fillStyle = alpha(th.faint, 0.95); ctx.textAlign = "right"; ctx.fillText(basis, st.w - 15, st.h - 14);
        }
        ctx.textAlign = "left";
    }

    // Leaders from the rail (docked to the right of the canvas) to their
    // nodes: elbow paths, one vertical channel per card, channels handed
    // out so leaders never cross when the cards stack in node order.
    // Mirror of AttentionRail.CountCrossings / AssignChannels in the model:
    // whether two elbow leaders cross depends on how card and node heights
    // interleave on screen, which changes with the camera, so channels are
    // assigned here from real positions on every draw (n is at most a dozen).
    function countCrossings(leaders, gap) {
        var segs = [], i, a, b;
        for (i = 0; i < leaders.length; i++) {
            var L = leaders[i], cx = -(L.channel + 1) * gap;
            segs.push([i, 0, L.cardY, cx, L.cardY]); segs.push([i, cx, L.cardY, cx, L.nodeY]); segs.push([i, cx, L.nodeY, -1e6, L.nodeY]);
        }
        var n = 0;
        for (a = 0; a < segs.length; a++) for (b = a + 1; b < segs.length; b++) {
            var s = segs[a], t = segs[b];
            if (s[0] === t[0]) continue;
            var sh = s[2] === s[4], th = t[2] === t[4];
            if (sh === th) continue;
            var h = sh ? s : t, v = sh ? t : s;
            var hx1 = Math.min(h[1], h[3]), hx2 = Math.max(h[1], h[3]), vy1 = Math.min(v[2], v[4]), vy2 = Math.max(v[2], v[4]);
            if (v[1] > hx1 && v[1] < hx2 && h[2] > vy1 && h[2] < vy2) n++;
        }
        return n;
    }
    function assignChannels(positions) {
        var order = [], i, pos;
        for (i = 0; i < positions.length; i++) {
            var bestPos = order.length, bestCross = Infinity;
            for (pos = 0; pos <= order.length; pos++) {
                var trial = order.slice(); trial.splice(pos, 0, i);
                var cross = countCrossings(trial.map(function (leader, channel) { return { cardY: positions[leader].cardY, nodeY: positions[leader].nodeY, channel: channel }; }), 8);
                if (cross < bestCross) { bestCross = cross; bestPos = pos; }
            }
            order.splice(bestPos, 0, i);
        }
        var channels = new Array(positions.length);
        for (i = 0; i < order.length; i++) channels[order[i]] = i;
        return channels;
    }

    // Leader route: rail card → channel at the right edge → the gutter above
    // or below the node's lane (whichever is nearer and free of lane labels)
    // → down/up onto the node's edge. Gutters hold nothing but lane names, so
    // the long horizontal run crosses no node; the short vertical drop may
    // cross a row of the same lane, and does so at a right angle, haloed.
    // Falls back to a direct run at the node's height when both gutters are
    // off-screen.
    function leaderPath(st, lead, p, chX, anchor, level, halfW, halfH, r, byLane) {
        var fr = st.frame, o = fr.opts, z = st.cam.zoom, ln = byLane[lead.lane];
        var top = p.y - (level === 2 ? halfH : r) - 2, bottom = p.y + (level === 2 ? halfH : r) + 2;
        var pts = [[st.w, anchor], [chX, anchor]];
        if (ln) {
            var ly0 = toScreen(st, ln.x, ln.y).y, ly1 = ly0 + ln.h * z, gap = o.laneGap * z;
            var above = ly0 - Math.max(6, gap * 0.5), below = ly1 + Math.max(6, gap * 0.5);
            var rowsAbove = Math.round((p.y - ly0) / (o.rowGap * z)), rowsBelow = Math.round((ly1 - p.y) / (o.rowGap * z)) - 1;
            var order = rowsAbove <= rowsBelow ? [above, below] : [below, above];
            for (var k = 0; k < order.length; k++) {
                var gy = order[k];
                if (gy < 28 || gy > st.h - 4) continue;
                if (!st.occ.free(Math.min(chX, p.x) - 2, gy - 4, Math.abs(chX - p.x) + 4, 8, ["body"])) continue;
                pts.push([chX, gy]); pts.push([p.x, gy]); pts.push([p.x, gy < p.y ? top : bottom]);
                return { pts: pts, tip: gy < p.y ? "down" : "up" };
            }
        }
        var nodeRight = p.x + (level === 2 ? halfW : r) + 3;
        pts.push([chX, p.y]); pts.push([nodeRight, p.y]);
        return { pts: pts, tip: "left" };
    }
    function drawLeaders(st, byId, level, halfW, halfH, r) {
        var fr = st.frame, ctx = st.ctx, th = st.theme;
        if (!fr.rail || !fr.rail.length) return;
        var cards = st.railCards || {};
        var byLane = {};
        for (var li = 0; li < (fr.lanes || []).length; li++) byLane[fr.lanes[li].id] = fr.lanes[li];
        var live = [], positions = [];
        for (var i = 0; i < fr.rail.length; i++) {
            var lead0 = fr.rail[i], node0 = byId[lead0.id], anchor0 = cards[lead0.id];
            if (!node0 || anchor0 === undefined) continue;
            live.push(lead0);
            positions.push({ cardY: anchor0, nodeY: toScreen(st, node0.x, node0.y).y });
        }
        var channels = assignChannels(positions);
        for (i = 0; i < live.length; i++) {
            var lead = live[i], node = byId[lead.id], anchor = cards[lead.id];
            var p = toScreen(st, node.x, node.y);
            var chX = st.w - 10 - channels[i] * 7;
            var hot = st.hoverRail === lead.id || st.hover === lead.id;
            var col = lead.top || hot ? th.danger : th.warn;
            var route = leaderPath(st, { id: lead.id, lane: node.lane }, p, chX, anchor, level, halfW, halfH, r, byLane);
            ctx.beginPath();
            for (var k = 0; k < route.pts.length; k++) { if (k === 0) ctx.moveTo(route.pts[k][0], route.pts[k][1]); else ctx.lineTo(route.pts[k][0], route.pts[k][1]); }
            halo(st, hot ? 5 : 4);
            // A fine dotted thread across the gutter; solid and bright only when the card or node is hovered.
            ctx.strokeStyle = alpha(col, hot ? 0.95 : lead.top ? 0.6 : 0.4);
            ctx.lineWidth = hot ? 2 : 1;
            ctx.setLineDash(hot ? [] : [2, 4]);
            ctx.stroke();
            ctx.setLineDash([]);
            ctx.fillStyle = ctx.strokeStyle;
            var e = route.pts[route.pts.length - 1];
            ctx.beginPath();
            if (route.tip === "down") { ctx.moveTo(e[0], e[1]); ctx.lineTo(e[0] - 3.5, e[1] - 7); ctx.lineTo(e[0] + 3.5, e[1] - 7); }
            else if (route.tip === "up") { ctx.moveTo(e[0], e[1]); ctx.lineTo(e[0] - 3.5, e[1] + 7); ctx.lineTo(e[0] + 3.5, e[1] + 7); }
            else { ctx.moveTo(e[0], e[1]); ctx.lineTo(e[0] + 7, e[1] - 3.5); ctx.lineTo(e[0] + 7, e[1] + 3.5); }
            ctx.closePath(); ctx.fill();
        }
    }

    function severityColor(th, severity) {
        var sv = (severity || "").toLowerCase();
        return sv === "important" || sv === "critical" ? th.danger : sv === "notable" ? th.warn : th.dim;
    }

    // A suggestion drawn as a ghost: dashed, translucent, tagged "suggested",
    // never a normal node in another colour. Joined to its parent by the edge
    // promotion would create. At bubble zoom it carries rationale and the
    // promote/dismiss decision.
    function drawSuggestionGhost(st, g, byId, level, halfW, halfH, r, open) {
        var ctx = st.ctx, th = st.theme, z = st.cam.zoom, o = st.frame.opts;
        var parent = byId[g.parent];
        var p = toScreen(st, g.x, g.y);
        if (p.x < -halfW - 200 || p.x > st.w + halfW + 200 || p.y < -halfH - 80 || p.y > st.h + halfH + 80) return;
        var hot = st.hover === "sug:" + g.id;
        var col = th.violet, sev = severityColor(th, g.severity);
        // The edge that would exist.
        if (parent) {
            var pp = toScreen(st, parent.x, parent.y);
            var ax = level === 2 ? pp.x + halfW : pp.x + r, bx = level === 2 ? p.x - halfW : p.x - r, mx = (ax + bx) / 2;
            ctx.strokeStyle = alpha(col, hot ? 0.8 : 0.4); ctx.lineWidth = 1.1; ctx.setLineDash([3, 4]);
            ctx.beginPath(); ctx.moveTo(ax, pp.y); ctx.bezierCurveTo(mx, pp.y, mx, p.y, bx, p.y); ctx.stroke(); ctx.setLineDash([]);
        }
        ctx.globalAlpha = hot ? 1 : 0.85;
        if (level < 2) {
            ctx.beginPath(); ctx.arc(p.x, p.y, r, 0, Math.PI * 2);
            halo(st, 5);
            ctx.setLineDash([2, 2]);
            ctx.fillStyle = alpha(col, 0.08); ctx.fill();
            ctx.lineWidth = 1.2; ctx.strokeStyle = alpha(col, 0.8); ctx.stroke(); ctx.setLineDash([]);
            ctx.fillStyle = col; ctx.font = Math.max(9, r * 1.1) + "px " + th.sans; ctx.textAlign = "center"; ctx.textBaseline = "middle";
            ctx.fillText("✦", p.x, p.y + 0.5);
            var labelW = 0;
            if (level === 1) {
                var gf = "italic " + Math.max(o.minTextPx, 10) + "px " + th.sans;
                ctx.font = gf;
                var groom = st.occ.roomRight(p.x + r + 5, p.y - 6, 12, Math.max(60, (o.colGap * z) - r * 2 - 14));
                var nm = fitText(ctx, g.title, groom >= 40 ? groom : Math.max(60, (o.colGap * z) - r * 2 - 14));
                var gAt = label(st, nm, gf, alpha(th.dim, 0.9), groom >= 40 ? [{ x: p.x + r + 5, y: p.y - 6 }] : [{ x: p.x - r, y: p.y + r + 3 }, { x: p.x + r + 5, y: p.y - 6 }], { h: 12 });
                if (gAt) labelW = gAt.x >= p.x ? gAt.w + 6 : 0;
            }
            if (g.folded) {
                label(st, "+" + g.folded + " more", "600 " + Math.max(o.minTextPx, 10) + "px " + th.mono, alpha(col, 0.9),
                    [{ x: p.x, y: p.y + r + 4 }, { x: p.x, y: p.y - r - 15 }, { x: p.x + r + 6 + labelW, y: p.y - 6 }], { h: 12, align: "center" });
            }
            st.hit.push({ id: "sug:" + g.id, sug: g.id, x: p.x - r - 2, y: p.y - r - 2, w: r * 2 + 4 + labelW, h: r * 2 + 4 });
            ctx.globalAlpha = 1;
            return;
        }
        var x = p.x - halfW, y = p.y - halfH, w = halfW * 2, h = halfH * 2;
        roundRect(ctx, x, y, w, h, 6 * Math.min(2, z));
        halo(st, 6);
        ctx.fillStyle = alpha(th.bg, 0.9); ctx.fill();
        ctx.fillStyle = alpha(col, hot ? 0.12 : 0.07); ctx.fill();
        ctx.setLineDash([5, 4]); ctx.lineWidth = hot ? 2 : 1.2; ctx.strokeStyle = alpha(col, hot ? 0.95 : 0.7); ctx.stroke(); ctx.setLineDash([]);
        st.hit.push({ id: "sug:" + g.id, sug: g.id, x: x, y: y, w: w, h: h });
        // Content in card units, like the bubble, so it grows with the box.
        ctx.save(); ctx.translate(x, y); ctx.scale(z, z);
        var W = o.nodeW, H = o.nodeH;
        ctx.textAlign = "left"; ctx.textBaseline = "middle";
        ctx.font = "700 4.6px " + th.sans; ctx.fillStyle = col; ctx.fillText("✦ suggested", 7, 8);
        var hx = 7 + ctx.measureText("✦ suggested").width + 4;
        ctx.font = "700 4px " + th.sans; ctx.fillStyle = sev; ctx.fillText(g.severity || "", hx, 8.2);
        hx += ctx.measureText(g.severity || "").width + 3;
        ctx.font = "3.8px " + th.mono; ctx.fillStyle = th.dim;
        ctx.fillText(fitText(ctx, [g.category, g.effort ? g.effort + " effort" : ""].filter(Boolean).join(" · "), W - hx - 8), hx, 8.3);
        ctx.font = "italic 700 4.8px " + th.sans; ctx.fillStyle = th.text; ctx.textBaseline = "top";
        var lines = wrapWords(ctx, g.title || "", W - 12, 3), ty = 14;
        for (var li = 0; li < lines.length; li++) { ctx.fillText(lines[li], 7, ty); ty += B.lineH; }
        if (open > 0) {
            ctx.globalAlpha = ctx.globalAlpha * open;
            ctx.font = "3.7px " + th.sans; ctx.fillStyle = th.dim;
            var rl = wrapWords(ctx, g.rationale || "", W - 12, 6);
            for (var ri = 0; ri < rl.length && ty + 5 < H - 30; ri++) { ctx.fillText(rl[ri], 7, ty + 2); ty += 5; }
            if (g.files && g.files.length && ty + 5 < H - 30) {
                ctx.font = "3.2px " + th.mono; ctx.fillStyle = th.faint;
                ctx.fillText(fitText(ctx, g.files.join(", "), W - 12), 7, ty + 3); ty += 5;
            }
            var buttons = [{ key: "promote", label: "Promote to work item", primary: true }, { key: "dismiss", label: "Dismiss", danger: true }, { key: "parent", label: "Go to source" }];
            var bx = 7, by = H - 20, bh = 9;
            ctx.textBaseline = "middle";
            for (var bi = 0; bi < buttons.length; bi++) {
                var btn = buttons[bi];
                ctx.font = "600 3.6px " + th.sans;
                var bw = ctx.measureText(btn.label).width + 7;
                if (bx + bw > W - 6) break;
                var bhot = st.hoverBtn === "sug:" + g.id + ":" + btn.key;
                roundRect(ctx, bx, by, bw, bh, 2.5);
                ctx.fillStyle = bhot ? th.hover : th.overlay; ctx.fill();
                ctx.lineWidth = 0.45; ctx.strokeStyle = btn.danger ? th.danger : btn.primary || bhot ? th.accent : th.borderStrong; ctx.stroke();
                ctx.fillStyle = btn.danger ? th.danger : btn.primary || bhot ? th.accentStrong : th.dim;
                ctx.textAlign = "center"; ctx.fillText(btn.label, bx + bw / 2, by + bh / 2 + 0.2); ctx.textAlign = "left";
                if (open > 0.6) st.hit.push({ id: "sug:" + g.id, sug: g.id, action: btn.key, x: x + bx * z, y: y + by * z, w: bw * z, h: bh * z });
                bx += bw + 2;
            }
        }
        ctx.font = "600 3.4px " + th.mono; ctx.fillStyle = alpha(col, 0.9); ctx.textAlign = "right"; ctx.textBaseline = "top";
        ctx.fillText(g.folded ? "+" + g.folded + " more" : "provisional", W - 5, H - 8);
        ctx.restore();
        ctx.globalAlpha = 1;
    }

    // The (+): an affordance attached to its parent, not a participant in
    // layout. A small ring just right of the node, joined by a stub — close
    // enough to be unmistakably this node's — that previews the relationship
    // ("a new box, depending on this one"), never the eventual coordinates;
    // where the created item lands is the layout's business. It asks the
    // occupancy registry for its spot like anything else drawn, and is
    // dropped rather than plated when the space to the right is taken.
    function canTakeDependent(n) { return !n.settled && n.actions && n.actions.indexOf("addDependent") >= 0; }
    function drawPlus(st, n, p, level, halfW, r) {
        var ctx = st.ctx, th = st.theme;
        var hot = st.hoverGhost === n.id || st.hover === n.id;
        var edge = level === 2 ? p.x + halfW : p.x + r, pr = 8, gap = 16;
        var spot = st.occ.place([{ x: edge + gap - pr, y: p.y - pr }, { x: edge + 4, y: p.y + (level === 2 ? halfW * 0.5 : r + 6) }], pr * 2 + 2, pr * 2 + 2, ["body", "label"]);
        if (spot.plated) return; // no room: no affordance, never clutter
        var cx = spot.x + pr, cy = spot.y + pr;
        ctx.strokeStyle = alpha(th.accent, hot ? 0.9 : 0.35); ctx.lineWidth = 1.2; ctx.setLineDash([]);
        ctx.beginPath(); ctx.moveTo(edge + 1, p.y); ctx.lineTo(cx - pr, cy); ctx.stroke();
        ctx.beginPath(); ctx.arc(cx, cy, pr, 0, Math.PI * 2); halo(st, 4);
        ctx.fillStyle = hot ? alpha(th.accent, 0.18) : th.bg; ctx.fill();
        ctx.strokeStyle = alpha(th.accent, hot ? 0.95 : 0.4); ctx.stroke();
        ctx.fillStyle = alpha(th.accentStrong, hot ? 1 : 0.6); ctx.font = "700 12px " + th.sans; ctx.textAlign = "center"; ctx.textBaseline = "middle";
        ctx.fillText("+", cx, cy + 0.5);
        if (hot) {
            // The relationship, previewed: a small dashed box past the ring, joined as a dependent would be.
            var bw = Math.max(26, Math.min(56, halfW * 0.6)), bh = Math.max(16, bw * 0.62);
            var bx = cx + pr + 10, by = cy - bh / 2;
            if (st.occ.free(bx, by, bw, bh, ["body"])) {
                ctx.setLineDash([3, 3]); ctx.strokeStyle = alpha(th.accent, 0.7); ctx.lineWidth = 1;
                ctx.beginPath(); ctx.moveTo(cx + pr, cy); ctx.lineTo(bx, cy); ctx.stroke();
                roundRect(ctx, bx, by, bw, bh, 3); ctx.fillStyle = alpha(th.accent, 0.08); ctx.fill(); ctx.stroke(); ctx.setLineDash([]);
                ctx.fillStyle = alpha(th.accentStrong, 0.9); ctx.font = "600 9px " + th.sans;
                ctx.fillText("new", bx + bw / 2, cy + 0.5);
            }
        }
        st.hit.push({ id: n.id, action: "addDependent", ghost: true, plus: true, x: cx - pr - 3, y: cy - pr - 3, w: pr * 2 + 6, h: pr * 2 + 6 });
    }

    // The card: the same box at every zoom from the card threshold up. Its
    // text cross-fades into the bubble content as it grows through the band.
    function drawCard(st, n, p, color, halfW, halfH, hovered, open, detail) {
        var ctx = st.ctx, th = st.theme, z = st.cam.zoom, o = st.frame.opts;
        var x = p.x - halfW, y = p.y - halfH, w = halfW * 2, h = halfH * 2;
        roundRect(ctx, x, y, w, h, 6 * Math.min(2, z));
        halo(st, 6);
        ctx.fillStyle = th.card; ctx.fill();
        ctx.lineWidth = hovered ? 2 : 1.2;
        if (n.settled) ctx.setLineDash([4, 3]);
        ctx.strokeStyle = hovered ? th.accent : alpha(color, 0.9); ctx.stroke(); ctx.setLineDash([]);
        ctx.fillStyle = color; ctx.fillRect(x, y + 4, Math.max(3, 1.5 * z), h - 8);

        // The card's text is gone before the bubble's is fully in, so the two
        // never sit on top of each other at half strength.
        var cardT = clamp(1 - open * 1.7, 0, 1), bubbleT = clamp((open - 0.35) / 0.65, 0, 1);
        if (cardT > 0) {
            ctx.globalAlpha = ctx.globalAlpha * cardT;
            var pad = 8 * z, tx = x + pad, maxW = w - pad * 2;
            var fs = Math.min(13, 12 * Math.max(1, z * 0.7));
            ctx.textBaseline = "top"; ctx.textAlign = "left";
            // The name leads, wrapped to as many lines as the top half of the card holds (never fewer than three).
            ctx.font = "600 " + fs + "px " + th.sans; ctx.fillStyle = n.settled ? th.dim : th.text;
            var lines = wrapWords(ctx, n.title || "", maxW, Math.max(3, Math.floor((h * 0.5) / (fs + 3)))), ly = y + 7 * z;
            for (var li = 0; li < lines.length; li++) { ctx.fillText(lines[li], tx, ly); ly += fs + 3; }
            // State · agent · age.
            ly += 3;
            ctx.font = "700 " + (fs - 1) + "px " + th.sans; ctx.fillStyle = color;
            ctx.fillText(n.glyph || "•", tx, ly);
            var gw = ctx.measureText(n.glyph || "•").width + 5;
            ctx.fillText(fitText(ctx, n.stateText || "", maxW - gw), tx + gw + (n.activity === "Running" ? 12 * z : 0), ly);
            var sw = ctx.measureText(n.stateText || "").width + (n.activity === "Running" ? 12 * z : 0) + 10;
            ctx.font = (fs - 1.5) + "px " + th.sans; ctx.fillStyle = th.dim;
            ctx.fillText(fitText(ctx, n.sub || "", maxW - gw - sw), tx + gw + sw, ly + 1);
            // What it waits on, or what waits on it — named, not numbered.
            ly += fs + 4;
            var rel = n.blockers && n.blockers.length ? "⇠ waits on " + n.blockers.map(function (b) { return b.title; }).join(", ")
                : n.waiting ? "⇢ " + n.waiting + " waiting on this" : "";
            if (rel && ly < y + h - 14) {
                ctx.font = "600 " + (fs - 1.5) + "px " + th.sans; ctx.fillStyle = alpha(th.orange, 0.95);
                ctx.fillText(fitText(ctx, rel, maxW), tx, ly);
            }
            // A small circuit from the state alone: where it is now, no path
            // implied (history is only fetched at bubble zoom). It grows into
            // the labelled circuit as the card grows.
            var mcy = y + h * 0.78, mr = Math.max(3, 1.6 * z), mx0 = tx + mr, mpitch = (maxW - mr * 2) / (STAGES.length - 1);
            ctx.lineWidth = 1; ctx.strokeStyle = alpha(th.borderStrong, 0.9);
            ctx.beginPath(); ctx.moveTo(mx0, mcy); ctx.lineTo(mx0 + mpitch * (STAGES.length - 1), mcy); ctx.stroke();
            for (var si = 0; si < STAGES.length; si++) {
                var msx = mx0 + si * mpitch, cur = STAGES[si] === n.stage;
                ctx.beginPath();
                if (STAGES[si] === "audit") { ctx.moveTo(msx, mcy - mr * 1.3); ctx.lineTo(msx + mr * 1.3, mcy); ctx.lineTo(msx, mcy + mr * 1.3); ctx.lineTo(msx - mr * 1.3, mcy); ctx.closePath(); }
                else ctx.arc(msx, mcy, mr, 0, Math.PI * 2);
                ctx.fillStyle = cur ? (n.settled ? th.faint : color) : th.card; ctx.fill();
                ctx.lineWidth = cur ? 1.6 : 1; ctx.strokeStyle = cur ? (n.settled ? th.faint : color) : th.borderStrong; ctx.stroke();
                if (cur) {
                    ctx.font = "600 " + Math.max(9, fs - 3) + "px " + th.sans; ctx.fillStyle = th.dim; ctx.textAlign = "center"; ctx.textBaseline = "top";
                    ctx.fillText(STAGES[si], msx, mcy + mr + 3);
                    ctx.textAlign = "left"; ctx.textBaseline = "top";
                }
            }
            // The id: discrete, secondary, bottom-right.
            ctx.font = Math.max(9, fs - 3) + "px " + th.mono; ctx.fillStyle = alpha(th.faint, 0.9); ctx.textAlign = "right"; ctx.textBaseline = "top";
            ctx.fillText(n.id.slice(0, 8), x + w - pad * 0.6, y + 5 * z);
            ctx.textAlign = "left";
            ctx.globalAlpha = n.settled ? 0.6 : 1;
        }
        if (bubbleT > 0) drawBubble(st, n, x, y, color, bubbleT, detail);
    }

    // Bubble content, drawn in card units under the zoom transform so it is
    // literally the card's content at a size where it can be read.
    function drawBubble(st, n, x, y, color, open, detail) {
        var ctx = st.ctx, th = st.theme, z = st.cam.zoom, o = st.frame.opts;
        var W = o.nodeW, H = o.nodeH;
        ctx.save();
        ctx.translate(x, y);
        ctx.scale(z, z);
        ctx.globalAlpha = open * (n.settled ? 0.6 : 1);
        ctx.textBaseline = "middle"; ctx.textAlign = "left";

        // Header: state (with the working ring beside it), agent · age, then the id, faint.
        var hx = 7;
        ctx.font = "700 5px " + th.sans; ctx.fillStyle = color; ctx.fillText(n.glyph || "•", hx, 8);
        hx += ctx.measureText(n.glyph || "•").width + 2.2;
        if (n.activity === "Running") hx += 7;
        ctx.font = "700 4.4px " + th.sans; ctx.fillStyle = color;
        ctx.fillText(n.stateText || "", hx, 8);
        hx += ctx.measureText(n.stateText || "").width + 4;
        ctx.font = "3.8px " + th.sans; ctx.fillStyle = th.dim;
        var sub = fitText(ctx, n.sub || "", 60);
        ctx.fillText(sub, hx, 8.3);
        hx += ctx.measureText(sub).width + 4;
        ctx.font = "3.4px " + th.mono; ctx.fillStyle = alpha(th.faint, 0.9);
        ctx.fillText(n.id.slice(0, 8), hx, 8.4);

        var buttons = [{ key: "detail", label: "Detail", w: 20 }, { key: "more", label: "⋯", w: 9 }];
        var bx = W - 4;
        for (var bi = buttons.length - 1; bi >= 0; bi--) {
            var btn = buttons[bi];
            bx -= btn.w;
            var hot = st.hoverBtn === n.id + ":" + btn.key;
            roundRect(ctx, bx, 3.5, btn.w, 9, 2.5);
            ctx.fillStyle = hot ? th.hover : th.overlay; ctx.fill();
            ctx.lineWidth = 0.45; ctx.strokeStyle = hot ? th.accent : th.borderStrong; ctx.stroke();
            ctx.font = "600 3.6px " + th.sans; ctx.fillStyle = hot ? th.accentStrong : th.dim;
            ctx.textAlign = "center"; ctx.fillText(btn.label, bx + btn.w / 2, 8.2);
            ctx.textAlign = "left";
            if (open > 0.6) st.hit.push({ id: n.id, action: btn.key, x: x + bx * z, y: y + 3.5 * z, w: btn.w * z, h: 9 * z });
            bx -= 2;
        }

        // The name, in full, wrapped.
        ctx.font = "700 4.8px " + th.sans; ctx.fillStyle = th.text; ctx.textBaseline = "top";
        var isDecision = n.needsYou && detail && detail.decision;
        var lines = wrapWords(ctx, n.title || "", W - 12, isDecision ? 5 : 6), ty = 14;
        for (var li = 0; li < lines.length; li++) { ctx.fillText(lines[li], 7, ty); ty += B.lineH; }
        ctx.textBaseline = "middle";
        // A long name pushes the circuit down rather than being cut short.
        var titleExtra = Math.max(0, ty - (14 + B.titleLines * B.lineH));

        if (isDecision) { drawDecision(st, n, x, y, color, detail.decision, ty + 4); ctx.restore(); return; }

        // What it is doing; blockers named and clickable.
        var ay = ty + 3;
        ctx.font = "3.7px " + th.sans; ctx.fillStyle = th.dim;
        if (n.blockers && n.blockers.length) {
            ctx.fillText("⇠ waits on", 7, ay);
            var rx = 7 + ctx.measureText("⇠ waits on").width + 2;
            for (var bi2 = 0; bi2 < n.blockers.length && rx < W - 20; bi2++) {
                var bl = n.blockers[bi2];
                ctx.font = "600 3.7px " + th.sans; ctx.fillStyle = bl.onMap ? th.accentStrong : th.dim;
                var bt = fitText(ctx, bl.title + " (" + bl.state + ")", W - 12 - rx);
                ctx.fillText(bt, rx, ay);
                var bw = ctx.measureText(bt).width;
                if (bl.onMap && open > 0.6) st.hit.push({ id: bl.id, action: "goto", x: x + rx * z, y: y + (ay - 2.5) * z, w: bw * z, h: 5 * z });
                rx += bw + 4;
                ctx.font = "3.7px " + th.sans; ctx.fillStyle = th.dim;
            }
        } else {
            ctx.fillText(fitText(ctx, n.waiting ? "⇢ " + n.waiting + " waiting on this · " + (n.activityText || "") : (n.activityText || ""), W - 12), 7, ay);
        }

        var loading = !detail, history = loading ? "loading" : detail.history;
        var stages = {};
        if (!loading) for (var i = 0; i < detail.stages.length; i++) stages[detail.stages[i].key] = detail.stages[i];

        // The spine: plan → work → audit ⇒ merge → landed. Solid, continuing right.
        var cy = B.rowY + titleExtra, r = B.stageR, pos = [];
        for (i = 0; i < STAGES.length; i++) pos.push(B.stageX0 + i * B.stagePitch);
        ctx.lineWidth = 0.6;
        for (i = 0; i < STAGES.length - 1; i++) {
            var sa = stages[STAGES[i]], sb = stages[STAGES[i + 1]];
            var travelled = sa && sb && sa.status !== "notReached" && sb.status !== "notReached" && sb.status !== "unknown" && sa.status !== "unknown";
            ctx.strokeStyle = alpha(travelled ? th.ok : th.borderStrong, 0.9);
            ctx.beginPath(); ctx.moveTo(pos[i] + r + 1.2, cy); ctx.lineTo(pos[i + 1] - r - 1.2, cy); ctx.stroke();
            if (i === 2) { // the gate's pass path reads as the default continuation
                ctx.fillStyle = ctx.strokeStyle;
                var gx = pos[3] - r - 1.6;
                ctx.beginPath(); ctx.moveTo(gx, cy); ctx.lineTo(gx - 2.4, cy - 1.4); ctx.lineTo(gx - 2.4, cy + 1.4); ctx.closePath(); ctx.fill();
            }
        }

        // Return edges: dotted, above the spine, routed by the model into tiers.
        if (!loading) {
            for (i = 0; i < detail.loops.length; i++) {
                var lp = detail.loops[i];
                var fx = pos[lp.from], tx = pos[lp.to];
                if (fx === undefined || tx === undefined) continue;
                var tier = Math.min(B.maxTier, lp.tier);
                var col = lp.kind === "rework" ? th.orange : lp.kind === "conflict" ? th.danger : lp.kind === "retry" ? th.info : th.faint;
                var apex = cy - r - B.tierBase - tier * B.tierGap;
                ctx.strokeStyle = alpha(col, 0.95); ctx.lineWidth = 0.55;
                ctx.setLineDash(lp.kind === "interruption" ? [0.8, 1.2] : [1.6, 1.2]);
                ctx.beginPath();
                if (fx === tx) {
                    ctx.arc(fx, apex + 2.6, 2.6, Math.PI * 0.15, Math.PI * 1.85);
                } else {
                    ctx.moveTo(fx, cy - r);
                    ctx.bezierCurveTo(fx, apex - 3, tx, apex - 3, tx, cy - r);
                }
                ctx.stroke(); ctx.setLineDash([]);
                ctx.fillStyle = ctx.strokeStyle;
                var ay2 = fx === tx ? apex + 5.2 : cy - r - 0.4;
                ctx.beginPath();
                ctx.moveTo(tx, ay2); ctx.lineTo(tx - 1.2, ay2 - 2.2); ctx.lineTo(tx + 1.2, ay2 - 2.2); ctx.closePath(); ctx.fill();
                var lx = B.stageX0 + lp.labelCenter * B.stagePitch, ly = apex - 0.7;
                ctx.font = "700 3.2px " + th.mono;
                var lw = ctx.measureText(lp.label).width + 3;
                roundRect(ctx, lx - lw / 2, ly - 2.3, lw, 4.6, 2.3);
                ctx.fillStyle = th.card; ctx.fill(); ctx.strokeStyle = alpha(col, 0.9); ctx.lineWidth = 0.35; ctx.stroke();
                ctx.fillStyle = col; ctx.textAlign = "center"; ctx.fillText(lp.label, lx, ly + 0.2);
                ctx.textAlign = "left";
            }
        }

        // Stage nodes with labels and evidence.
        for (i = 0; i < STAGES.length; i++) {
            var key = STAGES[i], s = stages[key], sx = pos[i];
            var status = loading ? "loading" : s ? s.status : "notReached";
            var isCur = status === "current" || status === "parked" || status === "failed";
            var fill = th.bg, stroke = th.borderStrong, glyph = "", glyphColor = th.faint, hatched = false;
            if (status === "visited") { stroke = th.ok; glyph = "✓"; glyphColor = th.ok; }
            else if (status === "current") { stroke = th.accent; fill = alpha(th.accent, 0.18); glyph = n.glyph || "●"; glyphColor = th.accentStrong; }
            else if (status === "parked") { stroke = th.warn; fill = alpha(th.warn, 0.15); glyph = "⏸"; glyphColor = th.warn; }
            else if (status === "failed") { stroke = th.danger; fill = alpha(th.danger, 0.15); glyph = "✗"; glyphColor = th.danger; }
            else if (status === "unknown" || status === "loading") { hatched = true; glyph = "?"; }
            if (key === "audit") { // the gate: a diamond, not a circle
                ctx.beginPath(); ctx.moveTo(sx, cy - r * 1.25); ctx.lineTo(sx + r * 1.25, cy); ctx.lineTo(sx, cy + r * 1.25); ctx.lineTo(sx - r * 1.25, cy); ctx.closePath();
            } else {
                ctx.beginPath(); ctx.arc(sx, cy, r, 0, Math.PI * 2);
            }
            ctx.fillStyle = fill; ctx.fill();
            if (hatched) {
                ctx.save(); ctx.clip();
                ctx.strokeStyle = alpha(th.faint, 0.5); ctx.lineWidth = 0.35;
                for (var hx2 = -r * 2; hx2 < r * 2; hx2 += 1.5) { ctx.beginPath(); ctx.moveTo(sx + hx2, cy - r * 1.3); ctx.lineTo(sx + hx2 + r * 2.6, cy + r * 1.3); ctx.stroke(); }
                ctx.restore();
                if (key === "audit") { ctx.beginPath(); ctx.moveTo(sx, cy - r * 1.25); ctx.lineTo(sx + r * 1.25, cy); ctx.lineTo(sx, cy + r * 1.25); ctx.lineTo(sx - r * 1.25, cy); ctx.closePath(); }
                else { ctx.beginPath(); ctx.arc(sx, cy, r, 0, Math.PI * 2); }
            }
            ctx.lineWidth = isCur ? 0.9 : 0.5; ctx.strokeStyle = stroke; ctx.stroke();
            if (isCur) { ctx.beginPath(); ctx.arc(sx, cy, r + 1.9, 0, Math.PI * 2); ctx.lineWidth = 0.35; ctx.strokeStyle = alpha(stroke, 0.6); ctx.stroke(); }
            if (glyph) {
                ctx.font = "700 4px " + th.sans; ctx.fillStyle = glyphColor;
                ctx.textAlign = "center"; ctx.fillText(glyph, sx, cy + 0.2);
            }
            ctx.font = (isCur ? "700 " : "600 ") + "4px " + th.sans;
            ctx.fillStyle = isCur ? th.text : status === "notReached" || hatched ? th.faint : th.dim;
            ctx.textAlign = "center"; ctx.textBaseline = "top";
            var lab = key.charAt(0).toUpperCase() + key.slice(1);
            if (s && s.visits > 1) lab += " ×" + s.visits;
            ctx.fillText(lab, sx, cy + r + 3.4);
            var det = status === "unknown" ? "unknown" : status === "loading" ? "…" : (s && s.detail) || "";
            if (det) {
                ctx.font = "3.1px " + th.mono; ctx.fillStyle = th.faint;
                ctx.fillText(fitText(ctx, det, B.stagePitch - 3), sx, cy + r + 8.6);
            }
            ctx.textAlign = "left"; ctx.textBaseline = "middle";
        }

        // Footer: the reading, or an honest note about what is not known.
        var foot = loading ? "Loading history…" : history === "unavailable" ? (detail.note || "History unavailable.") : history === "stateOnly" ? detail.note : detail.summary;
        ctx.font = "3.5px " + th.sans; ctx.fillStyle = history === "full" ? th.dim : th.warn;
        ctx.fillText(fitText(ctx, foot || "", W - 12), 7, H - 6);
        ctx.restore();
    }

    // A decision card: headline, the evidence, the actions — in card units,
    // continuing from the title. The operator decides here.
    function drawDecision(st, n, x, y, color, d, ty) {
        var ctx = st.ctx, th = st.theme, z = st.cam.zoom, o = st.frame.opts, W = o.nodeW, H = o.nodeH;
        ctx.textBaseline = "top"; ctx.textAlign = "left";
        ctx.font = "700 4.2px " + th.sans; ctx.fillStyle = color;
        ctx.fillText(fitText(ctx, d.headline || "", W - 12), 7, ty); ty += 6.5;

        var evidenceBottom = H - 34, lineH = 5.4;
        if (d.findings && d.findings.length) {
            for (var i = 0; i < d.findings.length && ty + lineH <= evidenceBottom; i++) {
                var f = d.findings[i];
                ctx.font = "700 3.6px " + th.sans; ctx.fillStyle = th.danger; ctx.fillText("‼", 7, ty + 0.3);
                ctx.font = "3.4px " + th.mono; ctx.fillStyle = th.dim;
                var who = fitText(ctx, f.auditor + " · " + f.severity, 46);
                ctx.fillText(who, 12, ty + 0.5);
                var ww = ctx.measureText(who).width + 3;
                ctx.font = "3.7px " + th.sans; ctx.fillStyle = th.text;
                ctx.fillText(fitText(ctx, f.title, W - 12 - 12 - ww), 12 + ww, ty);
                ty += lineH;
            }
        }
        if (d.findingsNote && ty + lineH <= evidenceBottom + 2) {
            ctx.font = (d.findings && d.findings.length ? "3.4px " : "600 3.6px ") + th.sans;
            ctx.fillStyle = d.findings && d.findings.length ? th.faint : th.warn;
            var noteLines = wrapWords(ctx, d.findingsNote, W - 14, 2);
            for (var ni = 0; ni < noteLines.length && ty + lineH <= evidenceBottom + 2; ni++) { ctx.fillText(noteLines[ni], 7, ty); ty += lineH - 0.8; }
        }
        if (d.openQuestion && ty + lineH <= evidenceBottom + 2) {
            ctx.font = "600 3.7px " + th.sans; ctx.fillStyle = th.text;
            var ql = wrapWords(ctx, "? " + d.openQuestion, W - 14, 2);
            for (var qi = 0; qi < ql.length && ty + lineH <= evidenceBottom + 2; qi++) { ctx.fillText(ql[qi], 7, ty); ty += lineH - 0.8; }
        }
        if (d.lastError && ty + lineH <= evidenceBottom + 2) {
            ctx.font = "3.3px " + th.mono; ctx.fillStyle = alpha(th.danger, 0.9);
            var el = wrapWords(ctx, "error: " + d.lastError, W - 14, 2);
            for (var ei = 0; ei < el.length && ty + lineH <= evidenceBottom + 2; ei++) { ctx.fillText(el[ei], 7, ty); ty += lineH - 1; }
        }

        // Actions row, drawn as buttons with hit regions.
        var byx = 7, byy = H - 24, bh = 9;
        ctx.textBaseline = "middle";
        for (var ai = 0; ai < (d.actions || []).length; ai++) {
            var act = d.actions[ai];
            if (act.key === "addDependent") continue; // that lives outside the bubble, as the (+) ghost
            ctx.font = "600 3.6px " + th.sans;
            var bw = ctx.measureText(act.label).width + 7;
            if (byx + bw > W - 6) break;
            var hot = st.hoverBtn === n.id + ":" + act.key;
            roundRect(ctx, byx, byy, bw, bh, 2.5);
            ctx.fillStyle = hot ? th.hover : th.overlay; ctx.fill();
            ctx.lineWidth = 0.45; ctx.strokeStyle = act.danger ? th.danger : hot ? th.accent : act.key === "answer" ? th.accentStrong : th.borderStrong; ctx.stroke();
            ctx.fillStyle = act.danger ? th.danger : hot ? th.accentStrong : act.key === "answer" ? th.accentStrong : th.text;
            ctx.textAlign = "center"; ctx.fillText(act.label, byx + bw / 2, byy + bh / 2 + 0.2); ctx.textAlign = "left";
            st.hit.push({ id: n.id, action: act.key, x: x + byx * z, y: y + byy * z, w: bw * z, h: bh * z });
            byx += bw + 2;
        }
        ctx.font = "3.4px " + th.sans; ctx.fillStyle = th.dim;
        ctx.fillText(fitText(ctx, d.waitingOn || n.activityText || "", W - 12), 7, H - 7);
    }

    // ── edges ────────────────────────────────────────────────────────────
    function segDist(px, py, x1, y1, x2, y2) {
        var dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy, t = l2 ? clamp(((px - x1) * dx + (py - y1) * dy) / l2, 0, 1) : 0;
        var qx = x1 + t * dx, qy = y1 + t * dy;
        return Math.sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
    }
    // Where the straight run a→b enters the canvas (Liang–Barsky), or null.
    function entryPoint(st, a, b) {
        var t0 = 0, t1 = 1, dx = b.x - a.x, dy = b.y - a.y;
        var p = [-dx, dx, -dy, dy], q = [a.x - 0, st.w - a.x, a.y - 24, st.h - a.y];
        for (var i = 0; i < 4; i++) {
            if (p[i] === 0) { if (q[i] < 0) return null; continue; }
            var t = q[i] / p[i];
            if (p[i] < 0) { if (t > t1) return null; if (t > t0) t0 = t; }
            else { if (t < t0) return null; if (t < t1) t1 = t; }
        }
        return { x: a.x + t0 * dx, y: a.y + t0 * dy };
    }
    function drawEdges(st, byId, level, halfW, halfH, r) {
        var fr = st.frame, ctx = st.ctx, th = st.theme, z = st.cam.zoom, o = fr.opts, i, k;
        var legendTop = st.legendRect ? st.legendRect.y - 4 : st.h;
        st.edgeSegs = [];
        st.stubs = {};
        // 1. Classify: endpoints, visibility, whether the source needs a stub.
        var live = [], bySource = {}, byTarget = {};
        for (i = 0; i < fr.edges.length; i++) {
            var e = fr.edges[i], a = byId[e.from], b = byId[e.to];
            if (!a || !b) continue;
            var pa = toScreen(st, a.x, a.y), pb = toScreen(st, b.x, b.y);
            var va = visibility(st, pa), vb = visibility(st, pb);
            var onScreenRun = Math.min(pa.x, pb.x) < st.w && Math.max(pa.x, pb.x) > 0 && Math.min(pa.y, pb.y) < st.h && Math.max(pa.y, pb.y) > 24;
            if (va <= 0.02 && vb <= 0.02 && !onScreenRun) continue;
            var ed = { e: e, a: a, b: b, pa: pa, pb: pb, va: va, vb: vb, stub: va <= 0.02 && vb > 0.02 && (pa.x < 0 || pa.y < 24 || pa.y > st.h) };
            live.push(ed);
            (bySource[e.from] = bySource[e.from] || []).push(ed);
            (byTarget[e.to] = byTarget[e.to] || []).push(ed);
        }
        // 2. Stubs: one per off-screen source, packed through the registry;
        //    the overflow collapses into one summary stub.
        var stubOrder = [], overflow = 0, sid;
        for (sid in bySource) { for (k = 0; k < bySource[sid].length; k++) if (bySource[sid][k].stub) { stubOrder.push(sid); break; } }
        stubOrder.sort(function (p1, p2) { return bySource[p1][0].pa.y - bySource[p2][0].pa.y; });
        ctx.font = "600 10px " + th.sans;
        for (i = 0; i < stubOrder.length; i++) {
            sid = stubOrder[i];
            var eds = bySource[sid], src = eds[0].a, entry = null;
            for (k = 0; k < eds.length && !entry; k++) if (eds[k].stub) entry = entryPoint(st, eds[k].pa, eds[k].pb);
            if (!entry) continue;
            var text = fitText(ctx, src.title || "", 150), tw = ctx.measureText(text).width + 16, th2 = 14;
            var sx = entry.x <= 0 ? 2 : entry.x >= st.w ? st.w - tw - 2 : clamp(entry.x - tw / 2, 2, st.w - tw - 2);
            var sy = clamp(entry.y - th2 / 2, 26, Math.max(26, legendTop - th2 - 2));
            var cands = [{ x: sx, y: sy }, { x: sx, y: sy + 16 }, { x: sx, y: sy - 16 }, { x: sx, y: sy + 32 }, { x: sx, y: sy - 32 }];
            var spot = st.occ.place(cands, tw, th2, ["body", "label", "stub"]);
            if (spot.plated) { overflow++; continue; }
            var stub = { id: sid, x: spot.x, y: spot.y, w: tw, h: th2, text: text, out: entry.x <= 0 ? "right" : entry.x >= st.w ? "left" : entry.y <= 24 ? "down" : "up" };
            st.stubs[sid] = stub;
            st.occ.claim(spot.x, spot.y, tw, th2, "stub");
        }
        if (overflow > 0) {
            var otext = "+" + overflow + " more source" + (overflow === 1 ? "" : "s") + " off-screen", ow = ctx.measureText(otext).width + 16;
            var ospot = st.occ.place([{ x: 2, y: Math.max(26, legendTop - 18) }, { x: 2, y: Math.max(26, legendTop - 36) }], ow, 14, ["body", "label", "stub"]);
            st.stubs["~overflow"] = { id: "~overflow", x: ospot.x, y: ospot.y, w: ow, h: 14, text: otext, out: "right", summary: true };
        }
        // 3. Bundles: fans of three or more edges from one source (or into one
        //    target) share a trunk and a vertical bus — subway style: each
        //    line keeps its own offset, runs the bus to its target's row and
        //    then straight in, passing under nearer boxes (their halos break
        //    it) rather than curving round them. The rest draw as curves.
        var drawn = {}, bundles = [];
        function collect(map, dir) {
            for (var key in map) {
                var ms = [];
                for (var m = 0; m < map[key].length; m++) { var ed2 = map[key][m]; if (!drawn[ed2.e.from + ">" + ed2.e.to]) ms.push(ed2); }
                if (ms.length < 3) continue;
                for (m = 0; m < ms.length; m++) drawn[ms[m].e.from + ">" + ms[m].e.to] = true;
                bundles.push({ dir: dir, members: ms });
            }
        }
        collect(bySource, "out"); collect(byTarget, "in");
        function edgeStyle(ed) {
            var blocked = ed.b.activity === "BlockedByDependency", settledEdge = ed.a.settled && ed.b.settled;
            var seen = ed.stub || st.stubs[ed.e.from] ? 1 : Math.max(ed.va, ed.vb);
            var hot = st.hoverEdge === ed.e.from + ">" + ed.e.to;
            var base = settledEdge ? 0.25 : blocked ? 0.55 : 0.45;
            return { color: hot ? th.accentStrong : blocked ? th.orange : th.faint, alpha: hot ? 1 : base * (0.1 + 0.9 * seen), width: hot ? 2 : level === 2 ? 1.4 : 1.1 };
        }
        function startOf(ed) {
            var stub = st.stubs[ed.e.from];
            if (stub) return stub.out === "right" ? { x: stub.x + stub.w, y: stub.y + stub.h / 2 } : stub.out === "left" ? { x: stub.x, y: stub.y + stub.h / 2 } : stub.out === "down" ? { x: stub.x + stub.w / 2, y: stub.y + stub.h } : { x: stub.x + stub.w / 2, y: stub.y };
            return { x: level === 2 ? ed.pa.x + halfW : ed.pa.x + r, y: ed.pa.y };
        }
        function endOf(ed) { return { x: level === 2 ? ed.pb.x - halfW : ed.pb.x - r, y: ed.pb.y }; }
        function arrow(ex, ey, col) { ctx.fillStyle = col; ctx.beginPath(); ctx.moveTo(ex, ey); ctx.lineTo(ex - 6, ey - 3.2); ctx.lineTo(ex - 6, ey + 3.2); ctx.closePath(); ctx.fill(); }
        // Bundles first (under the singles, which are fewer and more specific).
        for (i = 0; i < bundles.length; i++) {
            var bd = bundles[i], ms = bd.members, n = ms.length;
            var hub = bd.dir === "out" ? startOf(ms[0]) : endOf(ms[0]);
            var stubbed = bd.dir === "out" && !!st.stubs[ms[0].e.from];
            // Trunk: from the hub straight along x to the split, which sits
            // short of the nearest member's far end.
            var far = Infinity;
            for (k = 0; k < n; k++) { var fe = bd.dir === "out" ? endOf(ms[k]) : startOf(ms[k]); far = Math.min(far, Math.abs(fe.x - hub.x)); }
            var trunk = clamp(far * 0.45, 14, Math.max(14, 90 * Math.min(1, z)));
            var splitX = bd.dir === "out" ? hub.x + trunk : hub.x - trunk;
            ms.sort(function (m1, m2) { return (bd.dir === "out" ? m1.pb.y - m2.pb.y : m1.pa.y - m2.pa.y); });
            var pitch = Math.min(1.6, Math.max(0.15, ((level === 2 ? halfH * 2 : r * 2) - 4) / n));
            for (k = 0; k < n; k++) {
                var ed = ms[k], st1 = edgeStyle(ed), off = (k - (n - 1) / 2) * pitch;
                var from = bd.dir === "out" ? hub : startOf(ed), to = bd.dir === "out" ? endOf(ed) : hub;
                ctx.strokeStyle = alpha(st1.color, st1.alpha); ctx.lineWidth = st1.width; ctx.setLineDash([]);
                ctx.beginPath();
                var pts;
                if (bd.dir === "out") {
                    pts = [[from.x, from.y + off], [splitX + off, from.y + off], [splitX + off, to.y + off], [to.x, to.y + off]];
                } else {
                    pts = [[from.x, from.y + off], [splitX - off, from.y + off], [splitX - off, to.y + off], [to.x, to.y + off]];
                }
                ctx.moveTo(pts[0][0], pts[0][1]); for (var q = 1; q < pts.length; q++) ctx.lineTo(pts[q][0], pts[q][1]);
                ctx.stroke();
                arrow(pts[3][0], pts[3][1], ctx.strokeStyle);
                st.edgeSegs.push({ key: ed.e.from + ">" + ed.e.to, ed: ed, pts: pts });
            }
            if (stubbed) { /* the trunk starts at the stub: nothing more to draw */ }
        }
        // Singles: horizontal-tangent curves (or the model's arc over an obstacle).
        for (i = 0; i < live.length; i++) {
            var ed3 = live[i];
            if (drawn[ed3.e.from + ">" + ed3.e.to]) continue;
            var s3 = edgeStyle(ed3), a3 = startOf(ed3), b3 = endOf(ed3);
            ctx.strokeStyle = alpha(s3.color, s3.alpha); ctx.lineWidth = s3.width; ctx.setLineDash([]);
            ctx.beginPath(); ctx.moveTo(a3.x, a3.y);
            var mx = (a3.x + b3.x) / 2, segs;
            if (ed3.e.bend) {
                var apex = Math.min(ed3.pa.y, ed3.pb.y) + ed3.e.bend * z, dx3 = (b3.x - a3.x) / 3;
                ctx.bezierCurveTo(a3.x + dx3, apex, b3.x - dx3, apex, b3.x, b3.y);
                segs = [[a3.x, a3.y], [a3.x + dx3, apex], [b3.x - dx3, apex], [b3.x, b3.y]];
            } else {
                ctx.bezierCurveTo(mx, a3.y, mx, b3.y, b3.x, b3.y);
                segs = [[a3.x, a3.y], [mx, a3.y], [mx, b3.y], [b3.x, b3.y]];
            }
            ctx.stroke();
            arrow(b3.x, b3.y, ctx.strokeStyle);
            st.edgeSegs.push({ key: ed3.e.from + ">" + ed3.e.to, ed: ed3, pts: segs });
        }
        // 4. The stubs themselves, on top of the wires they emit.
        for (sid in st.stubs) {
            var sb = st.stubs[sid], shot = st.hoverStub === sid;
            roundRect(ctx, sb.x, sb.y, sb.w, sb.h, 4);
            ctx.fillStyle = alpha(th.overlay, 0.97); ctx.fill();
            ctx.lineWidth = 1; ctx.strokeStyle = shot ? th.accent : alpha(th.borderStrong, 0.9); ctx.stroke();
            ctx.font = "600 10px " + th.sans; ctx.textAlign = "left"; ctx.textBaseline = "middle";
            ctx.fillStyle = sb.summary ? th.dim : shot ? th.accentStrong : th.text;
            ctx.fillText((sb.summary ? "" : "◂ ") + sb.text, sb.x + 6, sb.y + sb.h / 2 + 0.5);
            if (!sb.summary) st.hit.push({ id: sid, action: "goto", stubHit: true, x: sb.x, y: sb.y, w: sb.w, h: sb.h });
        }
        // 5. The hovered edge, named.
        if (st.hoverEdge && st.pointer) {
            for (i = 0; i < st.edgeSegs.length; i++) {
                if (st.edgeSegs[i].key !== st.hoverEdge) continue;
                var hed = st.edgeSegs[i].ed;
                drawTooltip(st, { id: hed.e.to, x: (st.pointer.x - st.w / 2) / z + st.cam.x, y: (st.pointer.y - st.h / 2) / z + st.cam.y,
                    title: (hed.b.title || "") + " waits on " + (hed.a.title || ""), stateText: hed.b.activity === "BlockedByDependency" ? "blocked by it right now" : "dependency", sub: "" });
                break;
            }
        }
    }
    function edgeAt(st, sx, sy) {
        var best = null, bestD = 5;
        for (var i = 0; i < (st.edgeSegs || []).length; i++) {
            var sg = st.edgeSegs[i];
            for (var k = 0; k + 1 < sg.pts.length; k++) {
                var d = segDist(sx, sy, sg.pts[k][0], sg.pts[k][1], sg.pts[k + 1][0], sg.pts[k + 1][1]);
                if (d < bestD) { bestD = d; best = sg.key; }
            }
        }
        return best;
    }
    function fmtDay(d) {
        if (isNaN(d.getTime())) return "";
        return d.getDate() + " " + ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][d.getMonth()];
    }
    function fmtClock(iso) {
        var d = new Date(iso);
        if (isNaN(d.getTime())) return "";
        var now = new Date(), sameDay = d.toDateString() === now.toDateString();
        var hm = ("0" + d.getHours()).slice(-2) + ":" + ("0" + d.getMinutes()).slice(-2);
        if (sameDay) return hm;
        var days = (now - d) / 86400000;
        if (days < 6) return ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"][d.getDay()] + " " + hm;
        return d.getDate() + " " + ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"][d.getMonth()] + " " + hm;
    }
    // Mirror of TimeAxisScale.Read in the model: what a world x means.
    function fmtAgo(ms) {
        var m = Math.max(0, Math.round(ms / 60000));
        if (m < 1) return "just now";
        if (m < 60) return m + "m ago";
        var hrs = Math.floor(m / 60); if (hrs < 24) return hrs + "h " + (m % 60) + "m ago";
        var d = Math.floor(hrs / 24); return d + "d " + (hrs % 24) + "h ago";
    }
    function readAxis(fr, wx) {
        var o = fr.opts, ax = fr.axis, rate = o.colGap / Math.max(1, o.pastMinutesPerColumn), i;
        var now = ax.nowBucket ? new Date(ax.nowBucket.replace(" ", "T")) : new Date();
        if (Math.abs(wx) <= o.nodeW / 2) return { zone: "now" };
        if (wx > 0) {
            var best = 0, bestD = Infinity, near = Math.max(1, o.futureNear);
            for (var b = 0; b < Math.max(1, ax.futureBatches); b++) {
                var bx = b < near ? (b + 1) * o.colGap : (near + Math.max(0.1, o.futureCompression) * Math.log(1 + b - near + 1)) * o.colGap;
                var d = Math.abs(bx - wx); if (d < bestD) { bestD = d; best = b; }
            }
            return { zone: "future", batch: best };
        }
        for (i = 0; i < (ax.breaks || []).length; i++) { var bk = ax.breaks[i]; if (wx >= bk.x && wx <= bk.x + bk.w) return { zone: "past", brk: bk }; }
        // Mirror of TimeAxisScale.Decompress: axis minutes back to real minutes.
        var tau = Math.max(1, o.compressAfter || 30), kappa = Math.max(0.01, o.compression || 0.4);
        function decompress(m) { return m <= tau ? Math.max(0, m) : tau * Math.exp((m - tau) / (kappa * tau)); }
        var runs = ax.stretches || [], newest = -Infinity;
        for (i = 0; i < runs.length; i++) if (runs[i].x1 > newest) newest = runs[i].x1;
        var cutBetween = false;
        for (i = 0; i < (ax.breaks || []).length; i++) if (ax.breaks[i].x + ax.breaks[i].w > newest && ax.breaks[i].x <= 0) cutBetween = true;
        if (!cutBetween && wx > newest) { var at2 = new Date(now.getTime() - decompress(-wx / rate) * 60000); return { zone: "past", at: at2.toISOString(), ago: fmtAgo(now - at2) }; }
        for (i = 0; i < runs.length; i++) {
            var run = runs[i];
            if (wx < run.x0 - o.nodeW / 2 || wx > run.x1 + o.nodeW / 2) continue;
            var pts = run.pts || [];
            for (var q = 0; q + 1 < pts.length; q++) {
                if (wx <= pts[q][0] && wx >= pts[q + 1][0]) {
                    var at = new Date(new Date(pts[q][1]).getTime() - decompress((pts[q][0] - wx) / rate) * 60000);
                    return { zone: "past", at: at.toISOString(), ago: fmtAgo(now - at) };
                }
            }
            var edge = new Date(wx >= run.x1 ? run.t1 : run.t0);
            return { zone: "past", at: edge.toISOString(), ago: fmtAgo(now - edge) };
        }
        return { zone: "past" };
    }
    function clusterAsNode(cl) {
        return { id: cl.id, x: cl.x, y: cl.y, title: cl.count + " landed too close to draw apart", stateText: fmtClock(cl.from) + " → " + fmtClock(cl.to), sub: "click to zoom in · " + cl.members.slice(0, 3).map(function (m) { return m.title; }).join(" · ") + (cl.count > 3 ? " · …" : "") };
    }
    // Groups of landed items in one row whose screen spacing is under the
    // dot footprint at this zoom. Cards never group: the warp keeps a card's
    // width between landings in a lane, so at card zoom they always fit.
    function groupClose(st, fr, level, r) {
        if (level === 2) return [];
        var z = st.cam.zoom, rows = {}, i, n, out = [], openId = fr.camera ? fr.camera.open : null;
        for (i = 0; i < fr.nodes.length; i++) {
            n = fr.nodes[i];
            if (n.zone !== "past" || n.id === openId) continue;
            var key = n.lane + "@" + n.y;
            (rows[key] = rows[key] || []).push(n);
        }
        var need = r * 2 + 6;
        for (var key2 in rows) {
            var row = rows[key2].sort(function (a, b) { return a.x - b.x; }), run = [row[0]];
            for (i = 1; i <= row.length; i++) {
                if (i < row.length && (row[i].x - run[run.length - 1].x) * z < need) { run.push(row[i]); continue; }
                if (run.length > 1) {
                    var sx = 0, from = null, to = null;
                    for (var k = 0; k < run.length; k++) { sx += run[k].x; var at = run[k].landedAt || null; if (at && (!from || at < from)) from = at; if (at && (!to || at > to)) to = at; }
                    out.push({ id: "grp:" + run[0].id, x: sx / run.length, y: run[0].y, count: run.length, members: run, from: from, to: to });
                }
                if (i < row.length) run = [row[i]];
            }
        }
        return out;
    }
    // Zoom until a group's members sit apart as dots (never past the card
    // threshold: at that zoom the warp already spaces them), then tell the
    // page where the view went.
    function zoomToSplit(st, g) {
        var o = st.frame.opts, z = st.cam.zoom, d = Infinity, i;
        for (i = 1; i < g.members.length; i++) d = Math.min(d, g.members[i].x - g.members[i - 1].x);
        var cap = Math.max(z, o.fullDetailZoom * 0.98);
        for (var k = 0; k < 40 && z < cap; k++) {
            z = Math.min(cap, z * 1.2);
            if (d * z >= clamp(9 * z, 4, 16) * 2 + 6) break;
        }
        st.travel = null;
        setTarget(st, g.x, g.y, z, 500);
        if (st.travel) st.travel.onDone = function () { notifyManual(st); }; else notifyManual(st);
    }
    function drawCluster(st, cl, p, r, hovered) {
        var ctx = st.ctx, th = st.theme, o = st.frame.opts;
        ctx.globalAlpha = 0.85;
        var d = Math.max(2, r * 0.45);
        ctx.beginPath(); ctx.rect(p.x - r * 0.9, p.y - r * 0.9 - d * 2, r * 1.8 + d * 2, r * 1.8 + d * 2); halo(st, 5);
        for (var s2 = 2; s2 >= 0; s2--) {
            ctx.beginPath(); ctx.rect(p.x - r * 0.9 + d * s2, p.y - r * 0.9 - d * s2, r * 1.8, r * 1.8);
            ctx.fillStyle = s2 === 0 ? alpha(th.faint, 0.55) : th.bg; ctx.fill();
            ctx.lineWidth = s2 === 0 ? (hovered ? 2.2 : 1.6) : 1;
            if (s2 > 0) ctx.setLineDash([3, 2]);
            ctx.strokeStyle = hovered && s2 === 0 ? th.accent : th.faint; ctx.stroke(); ctx.setLineDash([]);
        }
        ctx.globalAlpha = 1;
        // The count is the point: always legible, never mistaken for one item.
        var badge = "×" + cl.count, bf = "700 " + Math.max(o.minTextPx, 10) + "px " + th.mono;
        var bAt = label(st, badge, bf, alpha(th.dim, 0.95), [{ x: p.x + r + d * 2 + 4, y: p.y - 6 }, { x: p.x - r, y: p.y + r + 3 }, { x: p.x - r, y: p.y - r - d * 2 - 15 }], { h: 12, plate: true, plateAlpha: 0.6 });
        var bw = bAt ? bAt.w : 0;
        st.hit.push({ id: cl.id, grp: cl, x: p.x - r - 2, y: p.y - r - d * 2 - 2, w: r * 2 + d * 2 + bw + 10, h: r * 2 + d * 2 + 4 });
    }
    function drawTooltip(st, n) {
        var ctx = st.ctx, th = st.theme, p = toScreen(st, n.x, n.y);
        ctx.font = "600 11px " + th.sans;
        var lines = wrapWords(ctx, n.title || n.id.slice(0, 8), 300, 3);
        ctx.font = "10px " + th.mono;
        var t2 = fitText(ctx, (n.stateText || "") + (n.sub ? " · " + n.sub : "") + " · " + n.id.slice(0, 8), 300);
        ctx.font = "600 11px " + th.sans;
        var w = 16, i;
        for (i = 0; i < lines.length; i++) w = Math.max(w, ctx.measureText(lines[i]).width + 16);
        ctx.font = "10px " + th.mono; w = Math.max(w, ctx.measureText(t2).width + 16);
        var h = 10 + lines.length * 14 + 14;
        var x = clamp(p.x + 14, 4, st.w - w - 4), y = clamp(p.y - h / 2, 4, st.h - h - 4);
        roundRect(ctx, x, y, w, h, 5);
        ctx.fillStyle = alpha(th.overlay, 0.97); ctx.fill();
        ctx.strokeStyle = th.borderStrong; ctx.lineWidth = 1; ctx.stroke();
        ctx.textAlign = "left"; ctx.textBaseline = "top";
        ctx.font = "600 11px " + th.sans; ctx.fillStyle = th.text;
        for (i = 0; i < lines.length; i++) ctx.fillText(lines[i], x + 8, y + 6 + i * 14);
        ctx.font = "10px " + th.mono; ctx.fillStyle = th.dim; ctx.fillText(t2, x + 8, y + 8 + lines.length * 14);
    }

    // ── fx: items being worked on right now ──────────────────────────────
    function fxSync(st) {
        if (!st.fxCtx) return;
        var want = st.running.length > 0 && !st.reduced && !document.hidden;
        if (!want) {
            fxClear(st);
            clearTimeout(st.fxTimer);
            if (st.fxRaf > 0) cancelAnimationFrame(st.fxRaf);
            st.fxRaf = 0;
            if (st.running.length > 0 && st.reduced) fxStatic(st);
            return;
        }
        if (!st.fxRaf) st.fxRaf = requestAnimationFrame(function (t) { fxFrame(st, t); });
    }
    function fxSchedule(st) {
        st.fxRaf = -1;
        st.fxTimer = setTimeout(function () {
            st.fxRaf = requestAnimationFrame(function (t) { fxFrame(st, t); });
        }, FX_FRAME_MS);
    }
    function fxClear(st) {
        // Full clear, not dirty rectangles: rings move with the camera and grow
        // with zoom, so recorded rects drift and leave streaks. One clear of a
        // transparent layer holding a handful of rings is the cheap, correct op.
        var c = st.fxCtx;
        c.setTransform(1, 0, 0, 1, 0, 0);
        c.clearRect(0, 0, st.fx.width, st.fx.height);
        c.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
    }
    function fxStatic(st) {
        var c = st.fxCtx;
        c.setTransform(st.dpr, 0, 0, st.dpr, 0, 0);
        for (var i = 0; i < st.running.length; i++) {
            var a = st.running[i];
            c.beginPath(); c.arc(a.x, a.y, a.r, 0, Math.PI * 2);
            c.lineWidth = 1.5; c.strokeStyle = alpha(a.color, 0.7); c.stroke();
        }
    }
    function fxFrame(st, t) {
        st.fxRaf = 0;
        if (!st.fxCtx || st.reduced || document.hidden || st.running.length === 0) { fxClear(st); return; }
        fxClear(st);
        var c = st.fxCtx, phase = (t % FX_PERIOD_MS) / FX_PERIOD_MS * Math.PI * 2;
        for (var i = 0; i < st.running.length; i++) {
            var a = st.running[i];
            c.beginPath(); c.arc(a.x, a.y, a.r, phase, phase + Math.PI * 1.5);
            c.lineWidth = a.ring ? 2 : 1.6; c.lineCap = "round"; c.strokeStyle = alpha(a.color, 0.9); c.stroke();
        }
        fxSchedule(st);
    }

    // ── sizing ───────────────────────────────────────────────────────────
    function resize(st) {
        var rect = st.wrap.getBoundingClientRect();
        var w = Math.max(1, Math.floor(rect.width)), h = Math.max(1, Math.floor(rect.height));
        var dpr = Math.max(1, Math.min(4, window.devicePixelRatio || 1));
        if (w === st.w && h === st.h && dpr === st.dpr && st.canvas.width === Math.round(w * dpr)) return;
        st.w = w; st.h = h; st.dpr = dpr;
        var canvases = [st.canvas, st.fx];
        for (var i = 0; i < canvases.length; i++) {
            var c = canvases[i];
            if (!c) continue;
            c.width = Math.round(w * dpr); c.height = Math.round(h * dpr);
            c.style.width = w + "px"; c.style.height = h + "px";
        }
        armDprWatch(st);
        if (st.dotNet) {
            st.dotNet.invokeMethodAsync("OnCanvasSize", w, h).catch(function () { /* late joiner; next poll */ });
        }
        measureRail(st);
        draw(st);
    }
    function armDprWatch(st) {
        if (st.dprMq && st.dprMq.removeEventListener) st.dprMq.removeEventListener("change", st.onDpr);
        if (!window.matchMedia) return;
        st.dprMq = window.matchMedia("(resolution: " + (window.devicePixelRatio || 1) + "dppx)");
        if (st.dprMq.addEventListener) st.dprMq.addEventListener("change", st.onDpr);
    }
    // Rail cards are HTML beside the canvas; their vertical anchors (canvas
    // coordinates) are read from the DOM whenever the frame or size changes.
    function measureRail(st) {
        st.railCards = {};
        try {
            var cr = st.canvas.getBoundingClientRect(), lg = st.wrap.querySelector(".fm-legend");
            if (lg) { var lr = lg.getBoundingClientRect(); st.legendRect = { x: lr.left - cr.left, y: lr.top - cr.top, w: lr.width, h: lr.height }; }
        } catch (e) { st.legendRect = null; }
        if (!st.rail) return;
        var base = st.canvas.getBoundingClientRect().top;
        var cards = st.rail.querySelectorAll("[data-rail-id]");
        for (var i = 0; i < cards.length; i++) {
            var r = cards[i].getBoundingClientRect();
            st.railCards[cards[i].getAttribute("data-rail-id")] = r.top + r.height / 2 - base;
        }
    }
    function notifyManual(st) {
        if (!st.dotNet) return;
        st.dotNet.invokeMethodAsync("OnManualViewport",
            Math.round(st.cam.x * 100) / 100, Math.round(st.cam.y * 100) / 100, Math.round(st.cam.zoom * 1000) / 1000)
            .catch(function () { /* circuit gone; harmless */ });
    }
    function invoke(st, method) {
        if (!st.dotNet) return;
        var args = Array.prototype.slice.call(arguments, 2);
        st.dotNet.invokeMethodAsync.apply(st.dotNet, [method].concat(args)).catch(function () { /* circuit gone; harmless */ });
    }
    function hitTest(st, sx, sy) {
        for (var i = st.hit.length - 1; i >= 0; i--) {
            var h = st.hit[i];
            if (sx >= h.x && sx <= h.x + h.w && sy >= h.y && sy <= h.y + h.h) return h;
        }
        return null;
    }

    // ── public API ───────────────────────────────────────────────────────
    window.codeyboxFleetMap = {
        init: function (canvasId, wrapId, fxId, railId, dotNet) {
            try {
                var canvas = document.getElementById(canvasId);
                var wrap = document.getElementById(wrapId) || (canvas && canvas.parentElement);
                var fx = fxId ? document.getElementById(fxId) : null;
                var rail = railId ? document.getElementById(railId) : null;
                if (!canvas || !wrap || !canvas.getContext) return false;
                var st = {
                    canvas: canvas, wrap: wrap, ctx: canvas.getContext("2d"), dotNet: dotNet, rail: rail,
                    fx: fx, fxCtx: fx && fx.getContext ? fx.getContext("2d") : null, fxRaf: 0, fxLast: 0,
                    frame: null, pulses: {}, hit: [], running: [], hover: null, hoverBtn: null, hoverGhost: null, hoverRail: null, railCards: {},
                    cam: { x: 0, y: 0, zoom: 1 }, travel: null, target: null,
                    w: 0, h: 0, dpr: 1, raf: 0, reduced: false, theme: readTheme(wrap)
                };
                maps[canvasId] = st;
                st.onDpr = function () { resize(st); };

                var mq = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
                var applyMq = function () {
                    var next = mq ? mq.matches : false;
                    if (next !== st.reduced) {
                        st.reduced = next;
                        if (next) {
                            if (st.raf) { cancelAnimationFrame(st.raf); st.raf = 0; }
                            if (st.travel) { st.cam.x = st.travel.tx; st.cam.y = st.travel.ty; st.cam.zoom = st.travel.tz; st.travel = null; }
                            st.pulses = {};
                        }
                        draw(st);
                        invoke(st, "OnReducedMotionChanged", next);
                    }
                };
                if (mq) { if (mq.addEventListener) mq.addEventListener("change", applyMq); else if (mq.addListener) mq.addListener(applyMq); }
                st.mq = mq;
                applyMq();

                st.onVisibility = function () { fxSync(st); };
                document.addEventListener("visibilitychange", st.onVisibility);

                if (window.MutationObserver) {
                    st.mo = new MutationObserver(function () { st.theme = readTheme(wrap); draw(st); });
                    st.mo.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
                    if (rail) {
                        st.railMo = new MutationObserver(function () { measureRail(st); draw(st); });
                        st.railMo.observe(rail, { childList: true, subtree: true, attributes: true });
                    }
                }

                var onResize = function () { resize(st); };
                if (window.ResizeObserver) { st.ro = new ResizeObserver(onResize); st.ro.observe(wrap); }
                window.addEventListener("resize", onResize);
                st.onResize = onResize;

                // Pointer: drag pans, click acts, hover reads.
                var dragging = false, lx = 0, ly = 0, moved = 0, raw = null;
                canvas.addEventListener("pointerdown", function (ev) {
                    if (ev.button !== 0) return;
                    dragging = true; moved = 0; lx = ev.clientX; ly = ev.clientY; raw = { x: st.cam.x, y: st.cam.y, zoom: st.cam.zoom };
                    try { canvas.setPointerCapture(ev.pointerId); } catch (e) { /* ignore */ }
                });
                canvas.addEventListener("pointermove", function (ev) {
                    var rect = canvas.getBoundingClientRect();
                    st.pointer = { x: ev.clientX - rect.left, y: ev.clientY - rect.top };
                    if (dragging) {
                        var dx = ev.clientX - lx, dy = ev.clientY - ly;
                        lx = ev.clientX; ly = ev.clientY;
                        moved += Math.abs(dx) + Math.abs(dy);
                        if (moved > 3) {
                            st.travel = null;
                            raw.x -= dx / st.cam.zoom; raw.y -= dy / st.cam.zoom;
                            var soft = softClamp(raw, st);
                            st.cam.x = soft.x; st.cam.y = soft.y;
                            draw(st);
                        }
                        return;
                    }
                    var h = hitTest(st, ev.clientX - rect.left, ev.clientY - rect.top);
                    var id = h ? h.id : null, btn = h && h.action && !h.ghost ? h.id + ":" + h.action : null, ghost = h && h.ghost ? h.id : null;
                    if (h && h.plus) id = h.id; // the (+) keeps its node hovered, so the preview stays
                    var stubId = h && h.stubHit ? h.id : null;
                    if (h && h.stubHit) { id = null; btn = null; }
                    var edgeKey = h ? null : edgeAt(st, ev.clientX - rect.left, ev.clientY - rect.top);
                    if (stubId !== st.hoverStub || edgeKey !== st.hoverEdge) { st.hoverStub = stubId; st.hoverEdge = edgeKey; }
                    if (h && h.sug && !h.action) btn = null;
                    // Hovering a blocker name keeps the parent card hovered.
                    if (h && h.action === "goto") { id = h.owner || st.hover; btn = (h.owner || st.hover) + ":" + h.id; }
                    if (id !== st.hover || btn !== st.hoverBtn || ghost !== st.hoverGhost) {
                        st.hover = id; st.hoverBtn = btn; st.hoverGhost = ghost;
                    }
                    canvas.style.cursor = (h && !h.brk) || st.hoverEdge ? "pointer" : "grab";
                    draw(st); // the ruler readout follows the pointer
                });
                var endDrag = function (ev) {
                    if (!dragging) return;
                    dragging = false;
                    if (moved > 3) {
                        // Settle back inside the bounds if the drag overshot, then report.
                        var back = clampCam(st.cam, st);
                        if (Math.abs(back.x - st.cam.x) > 0.5 || Math.abs(back.y - st.cam.y) > 0.5) {
                            setTarget(st, back.x, back.y, back.zoom, 260);
                            if (st.travel) st.travel.onDone = function () { notifyManual(st); }; else notifyManual(st);
                        } else notifyManual(st);
                        return;
                    }
                    var rect = canvas.getBoundingClientRect();
                    var h = hitTest(st, ev.clientX - rect.left, ev.clientY - rect.top);
                    if (!h) return;
                    if (h.sug) {
                        if (h.action) invoke(st, "OnGhostAction", h.sug, h.action);
                        else if (openFraction(st) >= 0.999) invoke(st, "OnGhostAction", h.sug, "promote");
                        else invoke(st, "OnNodeActivated", (st.frame.ghosts.filter(function (g) { return g.id === h.sug; })[0] || {}).parent || "");
                        return;
                    }
                    if (h.brk) return;
                    if (h.grp) { zoomToSplit(st, h.grp); return; }
                    if (h.action === "goto") invoke(st, "OnNodeActivated", h.id);
                    else if (h.action) invoke(st, "OnNodeAction", h.id, h.action === "more" ? "menu" : h.action);
                    else if (openFraction(st) >= 0.999) invoke(st, "OnNodeAction", h.id, "detail");
                    else invoke(st, "OnNodeActivated", h.id);
                };
                canvas.addEventListener("pointerup", endDrag);
                canvas.addEventListener("pointercancel", function () { dragging = false; });
                canvas.addEventListener("pointerleave", function () {
                    st.pointer = null;
                    st.hover = null; st.hoverBtn = null; st.hoverGhost = null; st.hoverEdge = null; st.hoverStub = null; canvas.style.cursor = "grab"; draw(st);
                });
                canvas.addEventListener("wheel", function (ev) {
                    ev.preventDefault();
                    st.travel = null;
                    var factor = Math.exp(-ev.deltaY * (ev.deltaMode === 1 ? 0.05 : 0.0015));
                    var before = st.cam.zoom;
                    st.cam.zoom = clamp(st.cam.zoom * factor, minZoom(st), 6);
                    var rect = canvas.getBoundingClientRect();
                    var mx = ev.clientX - rect.left - st.w / 2, my = ev.clientY - rect.top - st.h / 2;
                    st.cam.x += mx / before - mx / st.cam.zoom;
                    st.cam.y += my / before - my / st.cam.zoom;
                    var held = clampCam(st.cam, st); st.cam.x = held.x; st.cam.y = held.y; st.cam.zoom = held.zoom;
                    draw(st);
                    clearTimeout(st.wheelTimer);
                    st.wheelTimer = setTimeout(function () { notifyManual(st); }, 140);
                }, { passive: false });
                canvas.addEventListener("keydown", function (ev) {
                    var step = 60 / st.cam.zoom, handled = true;
                    if (ev.key === "Escape") invoke(st, "OnCloseItem");
                    else if (ev.key === "ArrowLeft") st.cam.x -= step;
                    else if (ev.key === "ArrowRight") st.cam.x += step;
                    else if (ev.key === "ArrowUp") st.cam.y -= step;
                    else if (ev.key === "ArrowDown") st.cam.y += step;
                    else if (ev.key === "+" || ev.key === "=") st.cam.zoom = clamp(st.cam.zoom * 1.2, minZoom(st), 6);
                    else if (ev.key === "-" || ev.key === "_") st.cam.zoom = clamp(st.cam.zoom / 1.2, minZoom(st), 6);
                    else handled = false;
                    if (!handled) return;
                    ev.preventDefault();
                    if (ev.key !== "Escape") { st.travel = null; var held2 = clampCam(st.cam, st); st.cam.x = held2.x; st.cam.y = held2.y; st.cam.zoom = held2.zoom; draw(st); notifyManual(st); }
                });

                resize(st);
                return true;
            } catch (e) {
                return false;
            }
        },

        render: function (canvasId, payloadJson) {
            var st = maps[canvasId];
            if (!st) return false;
            try {
                var fr = JSON.parse(payloadJson);
                var prev = st.frame;
                st.frame = fr;
                var now = performance.now();
                beginTween(st, prev, fr, now);
                if (fr.camera) {
                    var key = fr.camera.cx + "," + fr.camera.cy + "," + fr.camera.zoom;
                    if (key !== st.target) {
                        st.target = key;
                        // Under manual control the renderer owns the camera: a frame
                        // with no travel is the server echoing a viewport we sent, and
                        // a stale echo must never undo a newer pan or zoom.
                        if (!(fr.camera.manual && fr.camera.travelMs === 0)) {
                            setTarget(st, fr.camera.cx, fr.camera.cy, fr.camera.zoom, fr.camera.travelMs);
                        }
                    }
                }
                if (fr.transitions && !st.reduced) {
                    for (var i = 0; i < fr.transitions.length; i++) {
                        var item = fr.transitions[i].item;
                        if (item) st.pulses[item] = now + PULSE_MS;
                    }
                }
                for (var k in st.pulses) if (st.pulses[k] <= now) delete st.pulses[k];
                measureRail(st);
                draw(st);
                if (st.travel || st.tween || (!st.reduced && pulsesActive(st, now))) kick(st);
                return true;
            } catch (e) {
                return false;
            }
        },

        // Inspection hook (tests, diagnostics): the renderer's own camera and hover.
        state: function (canvasId) {
            var st = maps[canvasId];
            if (!st) return null;
            var fr = st.frame || {};
            return { x: st.cam.x, y: st.cam.y, zoom: st.cam.zoom, travelling: !!st.travel, tweening: !!st.tween, hover: st.hover, w: st.w, h: st.h, reduced: st.reduced, running: st.running.length,
                groups: Object.keys(st.byCluster || {}).length, breaks: fr.axis && fr.axis.breaks ? fr.axis.breaks.length : 0, frame: fr,
                occ: st.occ ? st.occ.rects : [], stubs: Object.keys(st.stubs || {}), edges: (st.edgeSegs || []).length, hoverEdge: st.hoverEdge,
                plus: (st.hit || []).filter(function (h) { return h.plus; }).map(function (h) { return { id: h.id, x: h.x, y: h.y }; }) };
        },

        // A rail card is hovered (or not): brighten its leader.
        highlight: function (canvasId, id) {
            var st = maps[canvasId];
            if (!st) return;
            st.hoverRail = id || null;
            draw(st);
        },

        // Operator preferences persisted per browser; the page reads them at
        // init so the same choice survives a reload.
        prefs: {
            get: function () {
                try { var raw = localStorage.getItem("cb-map-prefs"); return raw ? JSON.parse(raw) : null; } catch (e) { return null; }
            },
            set: function (json) {
                try { localStorage.setItem("cb-map-prefs", typeof json === "string" ? json : JSON.stringify(json)); } catch (e) { /* private mode */ }
            }
        },

        destroy: function (canvasId) {
            var st = maps[canvasId];
            if (!st) return;
            try {
                if (st.raf) cancelAnimationFrame(st.raf);
                clearTimeout(st.fxTimer);
                if (st.fxRaf > 0) cancelAnimationFrame(st.fxRaf);
                if (st.ro) st.ro.disconnect();
                if (st.mo) st.mo.disconnect();
                if (st.railMo) st.railMo.disconnect();
                if (st.onResize) window.removeEventListener("resize", st.onResize);
                if (st.onVisibility) document.removeEventListener("visibilitychange", st.onVisibility);
                if (st.dprMq && st.dprMq.removeEventListener) st.dprMq.removeEventListener("change", st.onDpr);
                clearTimeout(st.wheelTimer);
            } catch (e) { /* ignore */ }
            delete maps[canvasId];
        }
    };
})();
