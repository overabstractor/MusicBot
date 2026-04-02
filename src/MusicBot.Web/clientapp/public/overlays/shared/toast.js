// Shared toast notification system for MusicBot overlays
(function () {
  function _esc(t) {
    const d = document.createElement("div");
    d.textContent = String(t ?? "");
    return d.innerHTML;
  }

  window.showToast = function ({ icon = "♫", title = "", subtitle = "", type = "info", duration = 4000 } = {}) {
    let container = document.querySelector(".toast-container");
    if (!container) {
      container = document.createElement("div");
      container.className = "toast-container";
      document.body.appendChild(container);
    }

    const el = document.createElement("div");
    el.className = `toast toast-${type}`;
    el.innerHTML = `
      <div class="toast-icon">${icon}</div>
      <div class="toast-body">
        ${title    ? `<div class="toast-title">${_esc(title)}</div>`    : ""}
        ${subtitle ? `<div class="toast-sub">${_esc(subtitle)}</div>` : ""}
      </div>`;
    container.appendChild(el);

    setTimeout(() => {
      el.classList.add("toast-out");
      el.addEventListener("animationend", () => el.remove(), { once: true });
    }, duration);
  };
})();
