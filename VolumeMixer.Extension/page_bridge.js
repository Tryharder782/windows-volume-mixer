(() => {
  if (window.__vmPageBridgeInstalled) return;
  window.__vmPageBridgeInstalled = true;

  const handlers = new Map();

  try {
    if (typeof MediaSession !== 'undefined' && MediaSession.prototype) {
      const origSetHandler = MediaSession.prototype.setActionHandler;
      MediaSession.prototype.setActionHandler = function (action, callback) {
        if (action === 'previoustrack' || action === 'nexttrack') {
          if (typeof callback === 'function') {
            handlers.set(action, callback);
          } else {
            handlers.delete(action);
          }
          postMeta();
        }
        return origSetHandler.call(this, action, callback);
      };

      const desc = Object.getOwnPropertyDescriptor(MediaSession.prototype, 'metadata');
      if (desc && desc.set && desc.get) {
        Object.defineProperty(MediaSession.prototype, 'metadata', {
          configurable: true,
          enumerable: desc.enumerable,
          get() {
            return desc.get.call(this);
          },
          set(val) {
            desc.set.call(this, val);
            setTimeout(postMeta, 0);
          }
        });
      }
    }
  } catch {
    // Ignore restricted environments
  }

  function readMeta() {
    let title = '';
    let artist = '';
    let artwork = '';

    try {
      const meta = navigator.mediaSession?.metadata;
      if (meta) {
        title = meta.title || '';
        artist = meta.artist || meta.album || '';
        if (Array.isArray(meta.artwork) && meta.artwork.length > 0) {
          // Prefer JPG/PNG over WebP/AVIF for native WPF decoding, medium-to-high res
          const sorted = [...meta.artwork].sort((a, b) => {
            const aWebp = /\.(webp|avif)(\?|$)/i.test(a.src || '') || (a.type || '').includes('webp') ? 1 : 0;
            const bWebp = /\.(webp|avif)(\?|$)/i.test(b.src || '') || (b.type || '').includes('webp') ? 1 : 0;
            if (aWebp !== bWebp) return aWebp - bWebp;
            const pa = parseInt((a.sizes || '0').split('x')[0], 10) || 0;
            const pb = parseInt((b.sizes || '0').split('x')[0], 10) || 0;
            return pb - pa;
          });
          artwork = sorted[0]?.src || meta.artwork[0]?.src || '';
          const ytWebp = artwork.match(/https?:\/\/i\.ytimg\.com\/vi_webp\/([^/]+)\//i);
          if (ytWebp) {
            artwork = `https://i.ytimg.com/vi/${ytWebp[1]}/hqdefault.jpg`;
          }
        }
      }
    } catch {}

    const hasYtPrev = Boolean(document.querySelector('.ytp-prev-button:not([aria-disabled="true"])'));
    const hasYtNext = Boolean(document.querySelector('.ytp-next-button:not([aria-disabled="true"])'));

    return {
      title,
      artist,
      artwork,
      canPrev: handlers.has('previoustrack') || hasYtPrev,
      canNext: handlers.has('nexttrack') || hasYtNext
    };
  }

  function postMeta() {
    window.postMessage({ __vmMediaMeta: true, ...readMeta() }, '*');
  }

  window.addEventListener('message', (event) => {
    if (event.source !== window || !event.data) return;

    if (event.data.__vmGetMeta) {
      postMeta();
    } else if (event.data.__vmMediaAction === 'prev') {
      const fn = handlers.get('previoustrack');
      let handled = false;
      if (typeof fn === 'function') {
        try {
          fn({ action: 'previoustrack' });
          handled = true;
        } catch {}
      }
      if (!handled) {
        const btn = document.querySelector('.ytp-prev-button');
        if (btn) {
          btn.click();
          handled = true;
        }
      }
      window.postMessage({ __vmActionResult: 'prev', handled }, '*');
    } else if (event.data.__vmMediaAction === 'next') {
      const fn = handlers.get('nexttrack');
      let handled = false;
      if (typeof fn === 'function') {
        try {
          fn({ action: 'nexttrack' });
          handled = true;
        } catch {}
      }
      if (!handled) {
        const btn = document.querySelector('.ytp-next-button');
        if (btn) {
          btn.click();
          handled = true;
        }
      }
      window.postMessage({ __vmActionResult: 'next', handled }, '*');
    }
  });
})();
