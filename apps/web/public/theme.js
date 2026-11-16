// Applies the saved appearance before the first paint, so a dark choice never
// flashes light. The CSP admits only same-origin script files, not inline ones.
// ThemePreference (src/theme-preference.ts) owns the choice afterwards; keep the
// storage key and values in step with it.
(function () {
  var choice = null;
  try {
    choice = localStorage.getItem("braid.theme.v1");
  } catch (error) {}
  var dark =
    choice === "dark" ||
    (choice !== "light" &&
      matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
})();
