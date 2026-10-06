// Picks the theme before first paint by setting html[data-theme]; theme.css falls back to prefers-color-scheme
// when nothing is set. Order: an explicit ?theme=light|dark, then the embedding OpenEMR window's own theme
// stylesheet when it is same-origin (the reverse-proxy front door). A separate change
(function () {
  var root = document.documentElement;
  function valid(t) { return t === 'light' || t === 'dark' ? t : null; }
  function fromQuery() {
    try { return valid(new URLSearchParams(window.location.search).get('theme')); } catch (e) { return null; }
  }
  function fromHost() {
    try {
      var top = window.top;
      if (!top || top === window) { return null; }
      var links = top.document.querySelectorAll('link[rel="stylesheet"][href]');
      for (var i = 0; i < links.length; i++) {
        var m = /\/themes\/style_([a-z_]+)\.css/.exec(links[i].getAttribute('href') || '');
        if (m) { return m[1] === 'dark' ? 'dark' : 'light'; }
      }
    } catch (e) { /* cross-origin or no host: fall through */ }
    return null;
  }
  var theme = fromQuery() || fromHost();
  if (theme) { root.setAttribute('data-theme', theme); }
})();
