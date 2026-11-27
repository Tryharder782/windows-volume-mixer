(() => {
  let targetVolume = 1.0;
  let targetMuted = false;
  let isControlled = false;
  let lastHasMedia = false;
  let lastIsPlaying = false;
  let lastStateSignature = '';
  let lastTimeReportAt = 0;
  let scanTimer = null;

  let bridgeMeta = {
    title: '',
    artist: '',
    artwork: '',
    canPrev: false,
    canNext: false
  };

  let audioCtx = null;
  const gainNodes = new WeakMap();
  const failedGain = new WeakSet();

  function getMediaElements() {
    return Array.from(document.querySelectorAll('video, audio'));
  }

  function getPrimaryElement(elements) {
    if (!elements.length) return null;
    const playing = elements.find((el) => !el.paused && !el.ended && el.readyState > 1);
    if (playing) return playing;
    return elements.find((el) => el.readyState > 0) || elements[0];
  }

  function computeIsPlaying(elements) {
    return elements.some((el) => !el.paused && !el.ended && el.readyState > 1);
  }

  function getFallbackArtwork(primaryEl) {
    try {
      if (location.hostname.includes('youtube.com')) {
        const v = new URLSearchParams(location.search).get('v');
        if (v) return `https://i.ytimg.com/vi/${v}/hqdefault.jpg`;
      }
      if (primaryEl && primaryEl.poster) {
        return primaryEl.poster;
      }
      const og = document.querySelector('meta[property="og:image"]')?.getAttribute('content');
      if (og) return og;
    } catch {}
    return '';
  }

  function getTimelineInfo(primaryEl) {
    if (!primaryEl) {
      return { position: 0, duration: 0, isLive: false };
    }
    const rawDur = primaryEl.duration;
    const isLive = rawDur === Infinity || (Number.isNaN(rawDur) && !primaryEl.paused && primaryEl.readyState > 1);
    const duration = Number.isFinite(rawDur) && rawDur > 0 ? rawDur : 0;
    const position = Number.isFinite(primaryEl.currentTime) && primaryEl.currentTime >= 0
      ? (duration > 0 ? Math.min(primaryEl.currentTime, duration) : primaryEl.currentTime)
      : 0;
    return { position, duration, isLive };
  }

  function canUseWebAudio(el) {
    const src = el.currentSrc || el.src || '';
    if (!src || src.startsWith('blob:') || src.startsWith('data:') || src.startsWith('mediastream:')) {
      return true;
    }
    if (el.crossOrigin) return true;
    try {
      return new URL(src, location.href).origin === location.origin;
    } catch {
      return false;
    }
  }

  function ensureAudioContext() {
    if (!audioCtx) {
      const Ctx = window.AudioContext || window.webkitAudioContext;
      if (Ctx) {
        try {
          audioCtx = new Ctx();
        } catch {
          audioCtx = null;
        }
      }
    }
    if (audioCtx && audioCtx.state === 'suspended') {
      audioCtx.resume().then(() => {
        if (isControlled && audioCtx.state === 'running') {
          getMediaElements().forEach(applyToElement);
        }
      }).catch(() => {});
    }
    return audioCtx;
  }

  function getOrAttachGain(el) {
    if (gainNodes.has(el)) return gainNodes.get(el);
    if (failedGain.has(el)) return null;
    if (!canUseWebAudio(el)) {
      failedGain.add(el);
      return null;
    }

    const ctx = ensureAudioContext();
    if (!ctx || ctx.state !== 'running') return null;

    try {
      const source = ctx.createMediaElementSource(el);
      const gain = ctx.createGain();
      source.connect(gain);
      gain.connect(ctx.destination);
      gainNodes.set(el, gain);
      return gain;
    } catch {
      failedGain.add(el);
      return null;
    }
  }

  function applyToElement(el) {
    if (!isControlled) return;

    const effective = targetMuted ? 0.0 : targetVolume;
    const gain = getOrAttachGain(el);

    if (gain && audioCtx) {
      gain.gain.setValueAtTime(effective, audioCtx.currentTime);
      if (el.__vmTouchedFallback) {
        el.__vmTouchedFallback = false;
        try {
          el.volume = 1.0;
          el.muted = false;
        } catch {}
      }
      return;
    }

    try {
      el.__vmTouchedFallback = true;
      el.volume = targetVolume;
      el.muted = targetMuted;
    } catch {}
  }

  function buildPayload(elements) {
    const hasMedia = elements.length > 0;
    const isPlaying = computeIsPlaying(elements);
    const primary = getPrimaryElement(elements);
    const tl = getTimelineInfo(primary);
    const artworkUrl = bridgeMeta.artwork || getFallbackArtwork(primary);
    const mediaTitle = bridgeMeta.title || '';
    const mediaArtist = bridgeMeta.artist || '';

    return {
      hasMedia,
      isPlaying,
      volume: Math.round(targetVolume * 100),
      muted: targetMuted,
      mediaTitle,
      mediaArtist,
      artworkUrl,
      position: tl.position,
      duration: tl.duration,
      isLive: tl.isLive,
      canPrev: bridgeMeta.canPrev || tl.duration > 0,
      canNext: bridgeMeta.canNext || tl.duration > 0
    };
  }

  function reportState(elements, force = false) {
    const payload = buildPayload(elements);
    lastHasMedia = payload.hasMedia;
    lastIsPlaying = payload.isPlaying;

    const sig = `${payload.hasMedia}|${payload.isPlaying}|${payload.mediaTitle}|${payload.mediaArtist}|${payload.artworkUrl}|${Math.round(payload.duration)}|${payload.isLive}|${payload.canPrev}|${payload.canNext}`;
    if (!force && sig === lastStateSignature) {
      return;
    }
    lastStateSignature = sig;
    lastTimeReportAt = Date.now();

    try {
      chrome.runtime.sendMessage({
        type: 'mediaState',
        ...payload
      });
    } catch {
      // Extension context invalidated
    }
  }

  function hookElement(el) {
    if (el.__vmHooked) return;
    el.__vmHooked = true;

    el.addEventListener('play', () => {
      ensureAudioContext();
      if (isControlled) applyToElement(el);
      window.postMessage({ __vmGetMeta: true }, '*');
      reportState(getMediaElements(), true);
    }, { passive: true });

    el.addEventListener('pause', () => {
      reportState(getMediaElements(), true);
    }, { passive: true });

    el.addEventListener('ended', () => {
      reportState(getMediaElements(), true);
    }, { passive: true });

    el.addEventListener('seeked', () => {
      reportState(getMediaElements(), true);
    }, { passive: true });

    el.addEventListener('durationchange', () => {
      reportState(getMediaElements(), true);
    }, { passive: true });

    el.addEventListener('loadedmetadata', () => {
      if (isControlled) {
        setTimeout(() => applyToElement(el), 80);
      }
      window.postMessage({ __vmGetMeta: true }, '*');
      reportState(getMediaElements(), true);
    }, { passive: true });

    el.addEventListener('timeupdate', () => {
      const now = Date.now();
      if (now - lastTimeReportAt >= 3000) {
        reportState(getMediaElements(), true);
      }
    }, { passive: true });

    if (isControlled) {
      applyToElement(el);
    }
  }

  function scanMedia(force = false) {
    const elements = getMediaElements();
    elements.forEach(hookElement);
    reportState(elements, force);
  }

  function scheduleScan() {
    if (scanTimer !== null) return;
    scanTimer = setTimeout(() => {
      scanTimer = null;
      scanMedia(false);
    }, 200);
  }

  function triggerPageAction(action) {
    return new Promise((resolve) => {
      let settled = false;
      const onResult = (event) => {
        if (event.source !== window || !event.data || event.data.__vmActionResult !== action) return;
        settled = true;
        window.removeEventListener('message', onResult);
        resolve(Boolean(event.data.handled));
      };
      window.addEventListener('message', onResult);
      window.postMessage({ __vmMediaAction: action }, '*');
      setTimeout(() => {
        if (!settled) {
          window.removeEventListener('message', onResult);
          resolve(false);
        }
      }, 150);
    });
  }

  window.addEventListener('message', (event) => {
    if (event.source !== window || !event.data) return;
    if (event.data.__vmMediaMeta) {
      bridgeMeta = {
        title: event.data.title || '',
        artist: event.data.artist || '',
        artwork: event.data.artwork || '',
        canPrev: Boolean(event.data.canPrev),
        canNext: Boolean(event.data.canNext)
      };
      scanMedia(true);
    }
  });

  const unlockAudio = () => {
    if (isControlled) {
      ensureAudioContext();
    }
  };
  window.addEventListener('pointerdown', unlockAudio, { passive: true, capture: true });
  window.addEventListener('keydown', unlockAudio, { passive: true, capture: true });

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type === 'setVolume') {
      isControlled = true;
      targetVolume = Math.max(0, Math.min(1, (msg.volume ?? 100) / 100));
      targetMuted = Boolean(msg.muted);
      ensureAudioContext();
      getMediaElements().forEach((el) => {
        hookElement(el);
        applyToElement(el);
      });
      sendResponse({ ok: true });
    } else if (msg?.type === 'togglePlayPause') {
      const elements = getMediaElements();
      const playing = elements.filter((el) => !el.paused && !el.ended);
      if (playing.length > 0) {
        playing.forEach((el) => el.pause());
      } else if (elements.length > 0) {
        const primary = getPrimaryElement(elements);
        primary.play().catch(() => {});
      }
      setTimeout(() => reportState(getMediaElements(), true), 60);
      sendResponse({ ok: true });
    } else if (msg?.type === 'seek') {
      const elements = getMediaElements();
      const primary = getPrimaryElement(elements);
      if (primary && Number.isFinite(msg.position)) {
        try {
          primary.currentTime = Math.max(0, msg.position);
        } catch {}
        setTimeout(() => reportState(getMediaElements(), true), 40);
      }
      sendResponse({ ok: true });
    } else if (msg?.type === 'previous' || msg?.type === 'next') {
      const action = msg.type === 'previous' ? 'prev' : 'next';
      triggerPageAction(action).then((handled) => {
        if (!handled) {
          const primary = getPrimaryElement(getMediaElements());
          if (primary && Number.isFinite(primary.duration) && primary.duration > 0) {
            const delta = action === 'prev' ? -10 : 10;
            primary.currentTime = Math.max(0, Math.min(primary.duration, primary.currentTime + delta));
          }
        }
        setTimeout(() => reportState(getMediaElements(), true), 120);
      });
      sendResponse({ ok: true });
    } else if (msg?.type === 'probe') {
      window.postMessage({ __vmGetMeta: true }, '*');
      const elements = getMediaElements();
      elements.forEach(hookElement);
      const payload = buildPayload(elements);
      lastHasMedia = payload.hasMedia;
      lastIsPlaying = payload.isPlaying;
      sendResponse(payload);
    }
  });

  const observer = new MutationObserver(scheduleScan);
  if (document.documentElement) {
    observer.observe(document.documentElement, { childList: true, subtree: true });
  }

  window.postMessage({ __vmGetMeta: true }, '*');
  scanMedia(true);
})();
