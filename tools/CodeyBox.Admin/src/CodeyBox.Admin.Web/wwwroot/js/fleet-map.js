// Fleet map canvas renderer (vanilla JS, no frameworks).
//
// Cost model: requestAnimationFrame runs only while the camera is travelling
// or a recent transition is pulsing. A settled camera on a quiet fleet
// schedules zero frames — the screen can stay open for hours for free.
// Urgency stays visible as a static ring; only *change* moves.
//
// Colours mirror the admin status-chip tones in wwwroot/css/admin.css
// (.chip--active, .chip--queued, ...); keep the two in step.
(function () {
    "use strict";

    var PULSE_MS = 4000;
    var maps = {};

    // Dark-first palette; the canvas sits on the admin dark surface.
    var TONES = {
        active: "#4cc38a",
        queued: "#6cb8f0",
        review: "#b39df0",
        rework: "#f0a35e",
        wait: "#e5c07b",
        done: "#4cc38a",
        fail: "#e06c75",
        muted: "#8b93a3"
    };
    var EDGE = "rgba(139,147,163,0.45)";
    var TEXT = "#e6e9ef";
    var SUBTEXT = "#9aa3b2";
    var GRID = "rgba(139,147,163,0.10)";

    function toneColor(tone) {
        return TONES[tone] || TONES.muted;
    }

    function state(id) {
        return maps[id];
    }

    function worldToScreen(st, wx, wy) {
        return {
            x: (wx - st.cam.x) * st.cam.zoom + st.w / 2,
            y: (wy - st.cam.y) * st.cam.zoom + st.h / 2
        };
    }

    function kick(st) {
        if (!st.raf && !st.reducedMotionOK) {
            st.raf = requestAnimationFrame(function () { frame(st); });
        } else if (!st.raf && st.reducedMotionOK && st.needsDraw) {
            // Reduced motion: single still frame, no loop.
            st.needsDraw = false;
            draw(st);
        }
    }

    function settled(st) {
        var t = st.target;
        var dx = t.cx - st.cam.x, dy = t.cy - st.cam.y, dz = t.zoom - st.cam.zoom;
        return Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5 && Math.abs(dz) < 0.002;
    }

    function pulsesActive(st, now) {
        for (var k in st.pulses) {
            if (st.pulses[k] > now) return true;
        }
        return false;
    }

    function frame(st) {
        st.raf = 0;
        var now = performance.now();
        if (!settled(st)) {
            // Eased, continuous travel — never a cut.
            var ease = 0.12;
            st.cam.x += (st.target.cx - st.cam.x) * ease;
            st.cam.y += (st.target.cy - st.cam.y) * ease;
            st.cam.zoom += (st.target.zoom - st.cam.zoom) * ease;
            if (settled(st)) {
                st.cam.x = st.target.cx; st.cam.y = st.target.cy; st.cam.zoom = st.target.zoom;
            }
        }
        draw(st);
        if (!settled(st) || pulsesActive(st, now)) {
            st.raf = requestAnimationFrame(function () { frame(st); });
        }
        // Otherwise the loop ends here: zero idle cost until the next render().
    }

    function drawShape(ctx, shape, x, y, r) {
        ctx.beginPath();
        if (shape === "circle") {
            ctx.arc(x, y, r, 0, Math.PI * 2);
        } else if (shape === "square") {
            ctx.rect(x - r * 0.9, y - r * 0.9, r * 1.8, r * 1.8);
        } else if (shape === "diamond") {
            ctx.moveTo(x, y - r * 1.15); ctx.lineTo(x + r * 1.15, y);
            ctx.lineTo(x, y + r * 1.15); ctx.lineTo(x - r * 1.15, y);
            ctx.closePath();
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

    function draw(st) {
        var canvas = st.canvas, ctx = st.ctx;
        if (!canvas || !ctx) return;
        var dpr = window.devicePixelRatio || 1;
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        ctx.clearRect(0, 0, st.w, st.h);

        // Faint dot grid for map feel (screen space, culled by construction).
        ctx.fillStyle = GRID;
        var step = 56, ox = (st.w / 2 - st.cam.x * st.cam.zoom) % step, oy = (st.h / 2 - st.cam.y * st.cam.zoom) % step;
        if (ox < 0) ox += step;
        if (oy < 0) oy += step;
        for (var gx = ox; gx < st.w; gx += step) {
            for (var gy = oy; gy < st.h; gy += step) {
                ctx.fillRect(gx, gy, 1.5, 1.5);
            }
        }

        var frame = st.frame;
        if (!frame) return;
        var now = performance.now();
        var byId = {};
        var i, n;

        for (i = 0; i < frame.nodes.length; i++) byId[frame.nodes[i].id] = frame.nodes[i];

        // Edges first (under nodes), culled to the viewport.
        ctx.lineWidth = 1.2;
        for (i = 0; i < frame.edges.length; i++) {
            var e = frame.edges[i], a = byId[e.from], b = byId[e.to];
            if (!a || !b) continue;
            var pa = worldToScreen(st, a.x, a.y), pb = worldToScreen(st, b.x, b.y);
            if ((pa.x < -40 && pb.x < -40) || (pa.x > st.w + 40 && pb.x > st.w + 40) ||
                (pa.y < -40 && pb.y < -40) || (pa.y > st.h + 40 && pb.y > st.h + 40)) continue;
            ctx.strokeStyle = EDGE;
            ctx.beginPath(); ctx.moveTo(pa.x, pa.y); ctx.lineTo(pb.x, pb.y); ctx.stroke();
            var ang = Math.atan2(pb.y - pa.y, pb.x - pa.x), s = 6;
            ctx.fillStyle = EDGE;
            ctx.beginPath();
            ctx.moveTo(pb.x, pb.y);
            ctx.lineTo(pb.x - s * Math.cos(ang - 0.45), pb.y - s * Math.sin(ang - 0.45));
            ctx.lineTo(pb.x - s * Math.cos(ang + 0.45), pb.y - s * Math.sin(ang + 0.45));
            ctx.closePath(); ctx.fill();
        }

        for (i = 0; i < frame.nodes.length; i++) {
            n = frame.nodes[i];
            var p = worldToScreen(st, n.x, n.y);
            if (p.x < -80 || p.x > st.w + 80 || p.y < -80 || p.y > st.h + 80) continue;
            var r = Math.max(4, Math.min(34, 15 * st.cam.zoom));
            var color = toneColor(n.tone);

            drawShape(ctx, n.shape, p.x, p.y, r);
            ctx.fillStyle = "rgba(17,22,31,0.92)";
            ctx.fill();
            ctx.lineWidth = n.urgency ? 2.6 : 1.6;
            ctx.strokeStyle = color;
            ctx.stroke();

            // Urgency: a static extra ring always; an expanding pulse only
            // while a transition involving this node is fresh.
            if (n.urgency) {
                ctx.beginPath(); ctx.arc(p.x, p.y, r + 5, 0, Math.PI * 2);
                ctx.lineWidth = 1.2; ctx.strokeStyle = color; ctx.stroke();
            }
            var until = st.pulses[n.id] || 0;
            if (until > now && !st.reducedMotionOK) {
                var t = 1 - (until - now) / PULSE_MS;
                ctx.beginPath(); ctx.arc(p.x, p.y, r + 5 + t * 26, 0, Math.PI * 2);
                ctx.lineWidth = 2; ctx.strokeStyle = color;
                ctx.globalAlpha = 0.8 * (1 - t);
                ctx.stroke(); ctx.globalAlpha = 1;
            }

            // Glyph, always at least 10px: legible at any zoom.
            ctx.fillStyle = color;
            ctx.font = Math.max(10, r) + "px system-ui, sans-serif";
            ctx.textAlign = "center"; ctx.textBaseline = "middle";
            ctx.fillText(n.glyph || "•", p.x, p.y + 0.5);

            // Labels in screen space — zoom changes what shows, never how small.
            var ly = p.y + r + 12;
            ctx.textBaseline = "top";
            if (n.label) {
                ctx.font = (n.titleTextPx || 13) + "px system-ui, sans-serif";
                ctx.lineWidth = 3; ctx.strokeStyle = "rgba(11,14,20,0.85)";
                ctx.strokeText(n.label, p.x, ly);
                ctx.fillStyle = TEXT;
                ctx.fillText(n.label, p.x, ly);
                ly += (n.titleTextPx || 13) + 3;
                if (n.sub) {
                    ctx.font = (n.subTextPx || 11) + "px system-ui, sans-serif";
                    ctx.strokeText(n.sub, p.x, ly);
                    ctx.fillStyle = SUBTEXT;
                    ctx.fillText(n.sub, p.x, ly);
                }
            } else if (n.shortLabel) {
                ctx.font = Math.max(10, n.subTextPx || 11) + "px ui-monospace, monospace";
                ctx.lineWidth = 3; ctx.strokeStyle = "rgba(11,14,20,0.85)";
                ctx.strokeText(n.shortLabel, p.x, ly);
                ctx.fillStyle = SUBTEXT;
                ctx.fillText(n.shortLabel, p.x, ly);
            }
        }

        // Focus ring on the camera's item target (static outline, no motion).
        if (frame.camera && frame.camera.focus === "item" && byId[frame.camera.focusId]) {
            var f = byId[frame.camera.focusId], fp = worldToScreen(st, f.x, f.y);
            var fr = Math.max(4, Math.min(34, 15 * st.cam.zoom)) + 11;
            ctx.beginPath(); ctx.arc(fp.x, fp.y, fr, 0, Math.PI * 2);
            ctx.setLineDash([5, 4]); ctx.lineWidth = 1.4;
            ctx.strokeStyle = "rgba(230,233,239,0.6)"; ctx.stroke();
            ctx.setLineDash([]);
        }
    }

    function resize(st) {
        var rect = st.canvas.getBoundingClientRect();
        var w = Math.max(1, Math.round(rect.width)), h = Math.max(1, Math.round(rect.height));
        var dpr = window.devicePixelRatio || 1;
        st.canvas.width = Math.round(w * dpr);
        st.canvas.height = Math.round(h * dpr);
        st.w = w; st.h = h;
        if (st.dotNet) {
            try { st.dotNet.invokeMethodAsync("OnCanvasSize", w, h); } catch (e) { /* late joiner; next poll */ }
        }
        st.needsDraw = true;
        kick(st);
    }

    function notifyManual(st) {
        if (st.dotNet) {
            try {
                st.dotNet.invokeMethodAsync(
                    "OnManualViewport",
                    Math.round(st.cam.x * 100) / 100,
                    Math.round(st.cam.y * 100) / 100,
                    Math.round(st.cam.zoom * 1000) / 1000);
            } catch (e) { /* circuit gone; harmless */ }
        }
    }

    window.codeyboxFleetMap = {
        init: function (canvasId, dotNet) {
            try {
                var canvas = document.getElementById(canvasId);
                if (!canvas || !canvas.getContext) return false;
                var st = {
                    canvas: canvas, ctx: canvas.getContext("2d"),
                    dotNet: dotNet, frame: null, pulses: {},
                    cam: { x: 0, y: 0, zoom: 1 }, target: { cx: 0, cy: 0, zoom: 1 },
                    w: 800, h: 600, raf: 0, needsDraw: true,
                    reducedMotionOK: false, lastSizeReport: 0
                };
                maps[canvasId] = st;

                var mq = window.matchMedia ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
                var applyMq = function () {
                    var next = mq ? mq.matches : false;
                    if (next !== st.reducedMotionOK) {
                        st.reducedMotionOK = next;
                        if (next && st.raf) { cancelAnimationFrame(st.raf); st.raf = 0; }
                        if (st.dotNet) {
                            try { st.dotNet.invokeMethodAsync("OnReducedMotionChanged", next); } catch (e) { /* ignore */ }
                        }
                    }
                };
                if (mq) {
                    if (mq.addEventListener) mq.addEventListener("change", applyMq);
                    else if (mq.addListener) mq.addListener(applyMq);
                }
                st.mq = mq;
                applyMq();

                var reportSize = function () { resize(st); };
                if (window.ResizeObserver) {
                    st.ro = new ResizeObserver(reportSize);
                    st.ro.observe(canvas);
                }
                window.addEventListener("resize", reportSize);
                st.onResize = reportSize;

                var dragging = false, lx = 0, ly = 0, moved = false;
                canvas.addEventListener("pointerdown", function (ev) {
                    dragging = true; moved = false; lx = ev.clientX; ly = ev.clientY;
                    try { canvas.setPointerCapture(ev.pointerId); } catch (e) { /* ignore */ }
                });
                canvas.addEventListener("pointermove", function (ev) {
                    if (!dragging) return;
                    var dx = ev.clientX - lx, dy = ev.clientY - ly;
                    lx = ev.clientX; ly = ev.clientY;
                    if (Math.abs(dx) + Math.abs(dy) > 0) moved = true;
                    st.cam.x -= dx / st.cam.zoom; st.cam.y -= dy / st.cam.zoom;
                    st.target.cx = st.cam.x; st.target.cy = st.cam.y; st.target.zoom = st.cam.zoom;
                    st.needsDraw = true;
                    kick(st);
                });
                var endDrag = function () {
                    if (dragging && moved) notifyManual(st);
                    dragging = false;
                };
                canvas.addEventListener("pointerup", endDrag);
                canvas.addEventListener("pointercancel", function () { dragging = false; });
                canvas.addEventListener("wheel", function (ev) {
                    ev.preventDefault();
                    var factor = ev.deltaY > 0 ? 0.9 : 1.1;
                    var before = st.cam.zoom;
                    st.cam.zoom = Math.max(0.1, Math.min(4, st.cam.zoom * factor));
                    // Zoom towards the cursor.
                    var rect = canvas.getBoundingClientRect();
                    var mx = ev.clientX - rect.left - st.w / 2, my = ev.clientY - rect.top - st.h / 2;
                    st.cam.x += mx / before - mx / st.cam.zoom;
                    st.cam.y += my / before - my / st.cam.zoom;
                    st.target.cx = st.cam.x; st.target.cy = st.cam.y; st.target.zoom = st.cam.zoom;
                    st.needsDraw = true;
                    kick(st);
                    notifyManual(st);
                }, { passive: false });

                resize(st);
                return true;
            } catch (e) {
                return false;
            }
        },

        render: function (canvasId, payloadJson) {
            var st = state(canvasId);
            if (!st) return false;
            try {
                var frame = JSON.parse(payloadJson);
                st.frame = frame;
                if (frame.camera) {
                    st.target = { cx: frame.camera.cx, cy: frame.camera.cy, zoom: frame.camera.zoom };
                    if (st.reducedMotionOK) {
                        // Reduced motion: the cut is the point — jump, don't travel.
                        st.cam.x = st.target.cx; st.cam.y = st.target.cy; st.cam.zoom = st.target.zoom;
                    }
                }
                var now = performance.now();
                var hasPulse = false;
                if (frame.transitions) {
                    for (var i = 0; i < frame.transitions.length; i++) {
                        var item = frame.transitions[i].item;
                        if (item) { st.pulses[item] = now + PULSE_MS; hasPulse = true; }
                    }
                }
                for (var k in st.pulses) {
                    if (st.pulses[k] <= now) delete st.pulses[k];
                    else hasPulse = true;
                }
                st.needsDraw = true;
                if (!st.reducedMotionOK && (!settled(st) || hasPulse)) {
                    kick(st);
                } else if (st.reducedMotionOK) {
                    kick(st); // single still frame
                } else if (settled(st) && !hasPulse) {
                    draw(st); // one frame for the new data, then stop
                } else {
                    kick(st);
                }
                return true;
            } catch (e) {
                return false;
            }
        },

        destroy: function (canvasId) {
            var st = state(canvasId);
            if (!st) return;
            try {
                if (st.raf) cancelAnimationFrame(st.raf);
                if (st.ro) st.ro.disconnect();
                if (st.onResize) window.removeEventListener("resize", st.onResize);
            } catch (e) { /* ignore */ }
            delete maps[canvasId];
        }
    };
})();
