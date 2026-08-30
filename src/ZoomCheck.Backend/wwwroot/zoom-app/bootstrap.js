(function () {
  'use strict';

  // Strip transient callback parameters before loading the Zoom Apps SDK.
  if (window.location.search || window.location.hash) {
    window.history.replaceState(null, document.title, window.location.pathname);
  }
})();
