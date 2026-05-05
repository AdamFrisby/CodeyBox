// Auto-scroll helper for the live stdout panel.
// Scrolls to the bottom only when the user is already near the bottom
// (sticky-scroll behaviour — scrolling up suspends auto-scroll).
window.codeyboxScrollToBottom = function (el) {
    if (!el) return;
    var nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    if (nearBottom) el.scrollTop = el.scrollHeight;
};
