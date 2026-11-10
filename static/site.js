// The OpenAC site's small scripts: the theme switch, the download button's
// platform, the guides menu on phones, and search. The pages work without
// them; each part only improves what is already there.
(function () {
  "use strict";

  // ------------------------------------------------------------ theme

  function currentTheme() {
    var set = document.documentElement.dataset.theme;
    if (set) return set;
    return window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  }

  function setupTheme() {
    var button = document.getElementById("theme-toggle");
    if (!button) return;
    button.addEventListener("click", function () {
      var next = currentTheme() === "dark" ? "light" : "dark";
      document.documentElement.dataset.theme = next;
      try { localStorage.setItem("openac-theme", next); } catch (e) {}
    });
  }

  // ------------------------------------------------------------ download

  // Which download suits this visitor: "win", "linux", "mac-arm",
  // "mac-intel", or null (a phone, or unknown) to keep the guide link.
  function detectPlatform() {
    var ua = navigator.userAgent || "";
    var platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
    if (/iPhone|iPad|iPod|Android/i.test(ua)) return Promise.resolve(null);
    if (/Win/i.test(platform) || /Windows/i.test(ua)) return Promise.resolve("win");
    if (/Mac/i.test(platform) || /Mac OS X/i.test(ua)) {
      // Browsers on Intel and Apple silicon Macs report the same platform;
      // only some can tell the processor. Apple silicon when they can't.
      var data = navigator.userAgentData;
      if (data && data.getHighEntropyValues) {
        return data.getHighEntropyValues(["architecture"]).then(function (v) {
          return v.architecture === "x86" ? "mac-intel" : "mac-arm";
        }, function () { return "mac-arm"; });
      }
      return Promise.resolve("mac-arm");
    }
    if (/Linux|X11|CrOS/i.test(platform + " " + ua)) return Promise.resolve("linux");
    return Promise.resolve(null);
  }

  function setupDownload() {
    var box = document.getElementById("download");
    if (!box) return;
    detectPlatform().then(function (id) {
      if (!id) return;
      var url = box.getAttribute("data-" + id);
      var label = box.getAttribute("data-" + id + "-label");
      if (!url || !label) return;
      document.getElementById("download-button").href = url;
      document.getElementById("download-label").textContent = "Download for " + label;
    });
  }

  // ------------------------------------------------------------ guides menu

  function setupSidebar() {
    var menu = document.querySelector(".sidebar-menu");
    if (!menu) return;
    var narrow = window.matchMedia("(max-width: 860px)");
    function apply() { menu.open = !narrow.matches; }
    apply();
    if (narrow.addEventListener) narrow.addEventListener("change", apply);
  }

  // ------------------------------------------------------------ search

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement("script");
      s.src = src; s.onload = resolve; s.onerror = reject;
      document.head.appendChild(s);
    });
  }

  function escapeHtml(text) {
    return text.replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }

  // A short piece of the page's text around the first word searched for.
  function snippet(body, terms) {
    // The index keeps the text with its HTML entities; read it as text.
    var decoder = document.createElement("textarea");
    decoder.innerHTML = body;
    body = decoder.value.replace(/\s+/g, " ");
    var lower = body.toLowerCase();
    var at = -1;
    for (var i = 0; i < terms.length && at < 0; i++) at = lower.indexOf(terms[i]);
    var start = Math.max(0, at - 60);
    var text = body.slice(start, start + 180);
    return (start > 0 ? "…" : "") + text + (start + 180 < body.length ? "…" : "");
  }

  function setupSearch() {
    var input = document.getElementById("search");
    var results = document.getElementById("search-results");
    if (!input || !results) return;
    var index = null;
    var loading = null;

    function ensureIndex() {
      if (!loading) {
        loading = loadScript(input.dataset.engine)
          .then(function () { return loadScript(input.dataset.index); })
          .then(function () { index = window.elasticlunr.Index.load(window.searchIndex); });
      }
      return loading;
    }

    function hide() { results.hidden = true; }

    function run() {
      var term = input.value.trim();
      if (term.length < 2) { hide(); return; }
      ensureIndex().then(function () {
        var found = index.search(term, {
          bool: "AND", expand: true,
          fields: { title: { boost: 3 }, body: { boost: 1 } }
        }).slice(0, 8);
        var terms = term.toLowerCase().split(/\s+/);
        if (!found.length) {
          results.innerHTML = '<p class="r-none">Nothing found for “' + escapeHtml(term) + '”.</p>';
        } else {
          results.innerHTML = found.map(function (r) {
            var doc = index.documentStore.getDoc(r.ref);
            return '<a href="' + escapeHtml(r.ref) + '"><span class="r-title">' + escapeHtml(doc.title || r.ref) +
              '</span><span class="r-body">' + escapeHtml(snippet(doc.body || "", terms)) + "</span></a>";
          }).join("");
        }
        results.hidden = false;
      }, function () {
        results.innerHTML = '<p class="r-none">Search could not load.</p>';
        results.hidden = false;
      });
    }

    var timer = null;
    input.addEventListener("input", function () { clearTimeout(timer); timer = setTimeout(run, 150); });
    input.addEventListener("focus", function () { ensureIndex(); if (input.value.trim().length >= 2) run(); });
    input.addEventListener("keydown", function (e) {
      if (e.key === "Escape") { input.value = ""; hide(); input.blur(); }
      if (e.key === "Enter") { var first = results.querySelector("a"); if (first) window.location = first.href; }
    });
    document.addEventListener("click", function (e) {
      if (!results.contains(e.target) && e.target !== input) hide();
    });
  }

  function start() { setupTheme(); setupDownload(); setupSidebar(); setupSearch(); }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
