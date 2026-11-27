const WS_URL = 'ws://127.0.0.1:49152';

let ws = null;
let reconnectTimer = null;
let heartbeatTimer = null;
let syncTimer = null;

// tabId -> { frames: Map<frameId, frameMedia>, volume: number, muted: boolean, host: string }
const tabStates = new Map();

// Clear legacy per-site volumes so new tabs always default to 100%
chrome.storage.local.remove('siteVolumes').catch(() => {});

// url -> dataUrl (transcoded from webp/avif to jpeg for WPF compatibility)
const artworkDataCache = new Map();
const artworkPending = new Set();

function resolveCompatibleArtwork(url) {
  if (!url) return '';
  if (url.startsWith('data:')) return url;
  if (!/\.(webp|avif)(\?|$)/i.test(url) && !url.includes('/vi_webp/')) {
    return url;
  }

  if (artworkDataCache.has(url)) {
    return artworkDataCache.get(url);
  }

  if (!artworkPending.has(url)) {
    artworkPending.add(url);
    (async () => {
      try {
        const res = await fetch(url);
        if (!res.ok) throw new Error('fetch failed');
        const blob = await res.blob();
        const bmp = await createImageBitmap(blob);
        const maxSide = 96;
        const scale = Math.min(1, maxSide / Math.max(bmp.width || 1, bmp.height || 1));
        const w = Math.max(1, Math.round(bmp.width * scale));
        const h = Math.max(1, Math.round(bmp.height * scale));
        const canvas = new OffscreenCanvas(w, h);
        const ctx = canvas.getContext('2d');
        ctx.drawImage(bmp, 0, 0, w, h);
        bmp.close();
        const outBlob = await canvas.convertToBlob({ type: 'image/jpeg', quality: 0.9 });
        const buf = await outBlob.arrayBuffer();
        const bytes = new Uint8Array(buf);
        let bin = '';
        for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        artworkDataCache.set(url, `data:image/jpeg;base64,${btoa(bin)}`);
        scheduleSync();
      } catch {
        artworkDataCache.set(url, '');
      } finally {
        artworkPending.delete(url);
      }
    })();
  }

  return '';
}

function getHostname(url) {
  if (!url) return '';
  try {
    return new URL(url).hostname.toLowerCase();
  } catch {
    return '';
  }
}

function getOrCreateTabState(tabId) {
  let state = tabStates.get(tabId);
  if (!state) {
    state = { frames: new Map(), volume: 100, muted: false, host: '' };
    tabStates.set(tabId, state);
  }
  return state;
}

function getAggregatedMedia(state) {
  if (!state) {
    return {
      hasMedia: false,
      isPlaying: false,
      mediaTitle: '',
      mediaArtist: '',
      artworkUrl: '',
      position: 0,
      duration: 0,
      isLive: false,
      canPrev: false,
      canNext: false
    };
  }

  let best = null;
  let hasMedia = false;
  let isPlaying = false;

  for (const f of state.frames.values()) {
    if (f.hasMedia) hasMedia = true;
    if (f.isPlaying) isPlaying = true;
    if (f.hasMedia && (!best || (f.isPlaying && !best.isPlaying) || (f.duration > 0 && best.duration === 0))) {
      best = f;
    }
  }

  if (!best) {
    return {
      hasMedia,
      isPlaying,
      mediaTitle: '',
      mediaArtist: '',
      artworkUrl: '',
      position: 0,
      duration: 0,
      isLive: false,
      canPrev: false,
      canNext: false
    };
  }

  let position = best.position || 0;
  if (best.isPlaying && !best.isLive && best.duration > 0 && best.updatedAt) {
    position = Math.min(best.duration, position + (Date.now() - best.updatedAt) / 1000);
  }

  return {
    hasMedia,
    isPlaying,
    mediaTitle: best.mediaTitle || '',
    mediaArtist: best.mediaArtist || '',
    artworkUrl: best.artworkUrl || '',
    position,
    duration: best.duration || 0,
    isLive: Boolean(best.isLive),
    canPrev: Boolean(best.canPrev),
    canNext: Boolean(best.canNext)
  };
}

function detectBrowserName() {
  const ua = navigator.userAgent || '';
  if (ua.includes('Edg/')) return 'Edge';
  if (ua.includes('OPR/') || ua.includes('Opera')) return 'Opera';
  if (ua.includes('YaBrowser')) return 'Yandex';
  if (ua.includes('Vivaldi')) return 'Vivaldi';
  if (navigator.brave) return 'Brave';
  return 'Chrome';
}

const BROWSER_NAME = detectBrowserName();

function syncTabHostState(tab) {
  if (!tab?.id || !tab.url) return;
  const host = getHostname(tab.url);
  if (!host) return;

  const state = getOrCreateTabState(tab.id);
  const hostChanged = Boolean(state.host && state.host !== host);
  state.host = host;

  if (hostChanged) {
    state.volume = 100;
    state.muted = false;
    chrome.tabs.sendMessage(tab.id, {
      type: 'setVolume',
      volume: 100,
      muted: false
    }).catch(() => {});
  }
}

async function ensureInjectedAndSend(tabId, message) {
  try {
    await chrome.tabs.sendMessage(tabId, message);
  } catch {
    try {
      await chrome.scripting.executeScript({
        target: { tabId, allFrames: true },
        files: ['page_bridge.js'],
        world: 'MAIN'
      });
      await chrome.scripting.executeScript({
        target: { tabId, allFrames: true },
        files: ['content.js']
      });
      await chrome.tabs.sendMessage(tabId, message);
    } catch {}
  }
}

function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) {
    return;
  }

  try {
    ws = new WebSocket(WS_URL);
  } catch {
    scheduleReconnect();
    return;
  }

  ws.onopen = () => {
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    if (heartbeatTimer) clearInterval(heartbeatTimer);
    heartbeatTimer = setInterval(() => {
      if (ws && ws.readyState === WebSocket.OPEN) {
        try {
          ws.send(JSON.stringify({ type: 'ping' }));
        } catch {}
      }
    }, 20000);
    probeAndSyncAllTabs();
  };

  ws.onmessage = async (event) => {
    try {
      const msg = JSON.parse(event.data);
      if (msg.type === 'setVolume' && typeof msg.tabId === 'number') {
        const state = getOrCreateTabState(msg.tabId);
        state.volume = msg.volume ?? 100;
        state.muted = Boolean(msg.muted);

        chrome.tabs.sendMessage(msg.tabId, {
          type: 'setVolume',
          volume: state.volume,
          muted: state.muted
        }).catch(() => {});
      } else if (msg.type === 'togglePlayPause' && typeof msg.tabId === 'number') {
        ensureInjectedAndSend(msg.tabId, { type: 'togglePlayPause' });
      } else if (msg.type === 'previous' && typeof msg.tabId === 'number') {
        ensureInjectedAndSend(msg.tabId, { type: 'previous' });
      } else if (msg.type === 'next' && typeof msg.tabId === 'number') {
        ensureInjectedAndSend(msg.tabId, { type: 'next' });
      } else if (msg.type === 'seek' && typeof msg.tabId === 'number') {
        ensureInjectedAndSend(msg.tabId, { type: 'seek', position: Number(msg.position) || 0 });
      }
    } catch {
      // Ignore malformed message
    }
  };

  ws.onclose = () => {
    if (heartbeatTimer) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
    ws = null;
    scheduleReconnect();
  };

  ws.onerror = () => {
    try { ws?.close(); } catch {}
  };
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connect();
  }, 3000);
}

function scheduleSync() {
  if (syncTimer) return;
  syncTimer = setTimeout(() => {
    syncTimer = null;
    pushTabsSnapshot();
  }, 120);
}

function updateFrameState(tabId, frameId, payload) {
  const state = getOrCreateTabState(tabId);
  state.frames.set(frameId, {
    hasMedia: Boolean(payload.hasMedia),
    isPlaying: Boolean(payload.isPlaying),
    mediaTitle: payload.mediaTitle || '',
    mediaArtist: payload.mediaArtist || '',
    artworkUrl: payload.artworkUrl || '',
    position: Number(payload.position) || 0,
    duration: Number(payload.duration) || 0,
    isLive: Boolean(payload.isLive),
    canPrev: Boolean(payload.canPrev),
    canNext: Boolean(payload.canNext),
    updatedAt: Date.now()
  });
}

async function probeTab(tab) {
  if (!tab.id || !tab.url) return;
  if (tab.url.startsWith('chrome://') || tab.url.startsWith('edge://') || tab.url.startsWith('brave://') || tab.url.startsWith('about:')) return;

  syncTabHostState(tab);

  try {
    const res = await chrome.tabs.sendMessage(tab.id, { type: 'probe' });
    if (res) {
      updateFrameState(tab.id, 0, res);
    }
  } catch {
    try {
      await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        files: ['page_bridge.js'],
        world: 'MAIN'
      });
      await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        files: ['content.js']
      });
      const res = await chrome.tabs.sendMessage(tab.id, { type: 'probe' });
      if (res) {
        updateFrameState(tab.id, 0, res);
      }
    } catch {
      // Restricted page
    }
  }
}

async function probeAndSyncAllTabs() {
  try {
    const tabs = await chrome.tabs.query({});
    await Promise.all(tabs.map(probeTab));
  } catch {
    // Ignore query errors
  }
  pushTabsSnapshot();
}

async function pushTabsSnapshot() {
  if (!ws || ws.readyState !== WebSocket.OPEN) return;

  try {
    const tabs = await chrome.tabs.query({});
    const activeTabs = [];

    for (const tab of tabs) {
      if (!tab.id) continue;
      const state = tabStates.get(tab.id);
      const agg = getAggregatedMedia(state);
      const isAudible = Boolean(tab.audible);
      const hasMedia = Boolean(agg.hasMedia || isAudible);

      if (!isAudible && !hasMedia) continue;

      syncTabHostState(tab);
      const latest = tabStates.get(tab.id);

      activeTabs.push({
        id: tab.id,
        title: tab.title || 'Вкладка',
        favIconUrl: tab.favIconUrl || '',
        audible: isAudible,
        hasMedia,
        isPlaying: Boolean(agg.isPlaying || isAudible),
        volume: latest?.volume ?? 100,
        muted: latest?.muted ?? false,
        mediaTitle: agg.mediaTitle || tab.title || 'Вкладка',
        mediaArtist: agg.mediaArtist || BROWSER_NAME,
        artworkUrl: resolveCompatibleArtwork(agg.artworkUrl || ''),
        position: agg.position,
        duration: agg.duration,
        isLive: agg.isLive,
        canPrev: agg.canPrev,
        canNext: agg.canNext
      });
    }

    ws.send(JSON.stringify({
      type: 'tabs',
      browser: BROWSER_NAME,
      tabs: activeTabs
    }));
  } catch {
    // Ignore send errors
  }
}

chrome.runtime.onMessage.addListener((msg, sender) => {
  const tabId = sender.tab?.id;
  if (!tabId) return;

  if (msg?.type === 'mediaState') {
    const frameId = sender.frameId ?? 0;
    updateFrameState(tabId, frameId, msg);
    if (sender.tab) {
      syncTabHostState(sender.tab);
    }
    scheduleSync();
  }
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.audible !== undefined || changeInfo.title !== undefined || changeInfo.favIconUrl !== undefined || changeInfo.status === 'complete') {
    if (changeInfo.status === 'complete') {
      syncTabHostState(tab);
      const state = tabStates.get(tabId);
      if (state && (state.volume !== 100 || state.muted)) {
        chrome.tabs.sendMessage(tabId, {
          type: 'setVolume',
          volume: state.volume,
          muted: state.muted
        }).catch(() => {});
      }
    }
    scheduleSync();
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  if (tabStates.delete(tabId)) {
    scheduleSync();
  }
});

chrome.alarms.create('vm-keepalive', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === 'vm-keepalive') {
    connect();
  }
});

connect();
