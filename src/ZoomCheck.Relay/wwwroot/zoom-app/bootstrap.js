(function () {
  'use strict';

  // Zoom's OAuth redirect appends a short-lived authorization code. This app
  // does not exchange that code, so remove it before any third-party SDK asset
  // is requested and before the user can copy or bookmark the callback URL.
  if (window.location.search || window.location.hash) {
    window.history.replaceState(null, document.title, window.location.pathname);
  }
})();
