(() => {
  const storageKey = "lumarecode.appearance";
  const systemMedia = "(prefers-color-scheme: light)";
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
  root.dataset.appearance = appearance;

  window.addEventListener("DOMContentLoaded", () => {
    const picker = document.querySelector(".theme-picker");
    const select = picker.querySelector("select");
    const sources = document.querySelectorAll("source[data-light-screenshot]");

    function showAppearance() {
      root.dataset.appearance = appearance;
      select.value = appearance;
      for (const source of sources) {
        // Keep the native media query in System mode so images track live OS changes.
        source.media = appearance === "light" ? "all" : appearance === "dark" ? "not all" : systemMedia;
      }
    }

    showAppearance();
    picker.hidden = false;
    select.addEventListener("change", () => {
      appearance = select.value;
      showAppearance();
      try {
        window.localStorage.setItem(storageKey, appearance);
      } catch {
        // The chosen appearance still applies to this page.
      }
    });
  });
})();
