(() => {
  const storageKey = "lumarecode.appearance";
  const systemLight = window.matchMedia("(prefers-color-scheme: light)");
  const root = document.documentElement;
  let appearance = "system";

  try {
    const saved = window.localStorage.getItem(storageKey);
    if (saved === "light" || saved === "dark") {
      appearance = saved;
    }
  } catch {
    // The system preference remains usable when the browser blocks storage.
  }
  function applyPalette() {
    const light = appearance === "light" || (appearance === "system" && systemLight.matches);
    root.dataset.appearance = light ? "light" : "dark";
  }
  applyPalette();

  window.addEventListener("DOMContentLoaded", () => {
    const picker = document.querySelector(".theme-picker");
    const trigger = picker.querySelector("summary");
    const buttons = picker.querySelectorAll("button[data-mode]");
    const sources = document.querySelectorAll("source[data-light-screenshot]");

    function showAppearance() {
      applyPalette();
      for (const button of buttons) {
        const selected = button.dataset.mode === appearance;
        button.setAttribute("aria-pressed", String(selected));
        if (selected) {
          trigger.textContent = button.textContent;
          trigger.setAttribute("aria-label", `${picker.dataset.label} · ${button.textContent}`);
        }
      }
      for (const source of sources) {
        source.media = root.dataset.appearance === "light" ? "all" : "not all";
      }
    }

    showAppearance();
    picker.hidden = false;
    systemLight.addEventListener("change", showAppearance);
    picker.addEventListener("click", event => {
      const button = event.target.closest?.("button[data-mode]");
      if (!button) return;
      appearance = button.dataset.mode;
      showAppearance();
      picker.open = false;
      trigger.focus();
      try {
        window.localStorage.setItem(storageKey, appearance);
      } catch {
        // The chosen appearance still applies to this page.
      }
    });
    picker.addEventListener("keydown", event => {
      if (event.key === "Escape" && picker.open) {
        event.preventDefault();
        picker.open = false;
        trigger.focus();
      }
    });
    document.addEventListener("pointerdown", event => {
      if (!picker.contains(event.target)) picker.open = false;
    });
  });
})();
