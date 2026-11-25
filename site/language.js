(() => {
  const preferencesKey = "lumarecode.language";
  const supported = new Set(["en", "zh", "ja", "es"]);

  // Only the neutral entry point chooses a language. Direct links stay where they point.
  if (document.currentScript.hasAttribute("data-choose-language")) {
    const current = new URL(window.location.href);
    if (current.searchParams.get("lang") !== "en") {
      let preferred;
      try {
        preferred = window.localStorage.getItem(preferencesKey);
      } catch {
        // The browser can block local storage. Its own language list still works.
      }
      const languages = navigator.languages?.length ? navigator.languages : [navigator.language];
      const detected = languages
        .map(language => language?.toLowerCase().split("-")[0])
        .find(language => supported.has(language));
      const language = supported.has(preferred) ? preferred : detected ?? "en";
      if (language !== "en") {
        const destination = new URL(`${language}/`, current);
        destination.search = current.search;
        destination.hash = current.hash;
        window.location.replace(destination);
      }
    }
  }

  document.addEventListener("click", event => {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) {
      return;
    }
    const link = event.target.closest?.("a[data-language]");
    if (link) {
      try {
        window.localStorage.setItem(preferencesKey, link.dataset.language);
      } catch {
        // The link works even when the preference cannot be saved.
      }
    }
  });
})();
