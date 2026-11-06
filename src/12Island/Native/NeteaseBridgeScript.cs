namespace TwelveIsland.Native;

internal sealed partial class NeteaseCdpConnection
{
    // The page keeps a bridge until the player restarts, so a bridge with new abilities needs a new name; the script
    // below installs and looks for this one.
    internal const string BridgeObject = "__brimDeckMusicV2";
    internal const string BridgeScript = """
    (() => {
      const binding = '__brimDeckMediaPush';
      const withLeases = bridge => {
        if (bridge.leaseVersion === 1) return bridge;
        const attach = bridge.attach.bind(bridge), detach = bridge.detach.bind(bridge);
        const clients = new Map(), lifetime = 90000;
        // Upgrade an already installed bridge in place: do not add another native subscription set.
        // Older clients have a grace period to reconnect with the heartbeat-capable implementation.
        for (const name of Object.getOwnPropertyNames(window)) {
          if (/^__brimDeckMediaPush_[a-f0-9]{32}$/.test(name)) clients.set(name, Date.now() + lifetime);
        }
        const prune = keep => {
          const now = Date.now();
          for (const [name, until] of clients) {
            if (name !== keep && until < now) { clients.delete(name); detach(name); }
          }
        };
        bridge.attach = name => {
          prune(name);
          const result = attach(name);
          if (result.ready) clients.set(name, Date.now() + lifetime);
          return result;
        };
        bridge.keepalive = name => {
          prune(name);
          if (!clients.has(name)) return bridge.attach(name).ready;
          clients.set(name, Date.now() + lifetime);
          return true;
        };
        bridge.detach = name => { clients.delete(name); detach(name); };
        bridge.leaseVersion = 1;
        return bridge;
      };
      const existing = window.__brimDeckMusicV2;
      if (existing && existing.version === 2) return withLeases(existing).attach(binding);
      // Startup still changes window visibility and restores playback asynchronously.
      if (typeof window._enterAppTime === 'number') return { ready: false };
      let loader = window.__brimDeckMusicLoaderV1;
      const key = 'brimDeckMedia' + Date.now();
      if (!loader && window.webpackJsonp && typeof window.webpackJsonp.push === 'function') {
        const modules = {}; modules[key] = function (module, exports, require) { loader = require; };
        window.webpackJsonp.push([[key], modules, [[key]]]);
      } else if (!loader) {
        const chunks = Object.keys(window).find(k => k.startsWith('webpackChunk') && Array.isArray(window[k]));
        if (chunks) window[chunks].push([[key], {}, require => { loader = require; }]);
      }
      if (!loader || !loader.c) return { ready: false };
      window.__brimDeckMusicLoaderV1 = loader;
      // Inspect only already evaluated module exports, once. Do not execute arbitrary module factories.
      const exports = Object.values(loader.c).map(m => m && m.exports).filter(Boolean);
      const tool = exports.map(e => e.a).find(e => e && typeof e.getStore === 'function' && typeof e.getDispatch === 'function');
      const store = tool && tool.app && tool.app._store;
      const player = exports.map(e => e.AudioPlayer).find(e => e && typeof e.subscribePlayStatus === 'function');
      const storage = exports.map(e => e.b).find(e => e && e.lastPlaying && typeof e.lastPlaying.get === 'function');
      if (!store || typeof store.getState !== 'function' || typeof store.subscribe !== 'function' || typeof store.dispatch !== 'function') return { ready: false };
      if (!store.getState().playing) return { ready: false };
      // A bridge from an earlier TwelveIsland keeps its native subscriptions, which have no removal; its clients are
      // released so that it stops pushing.
      const older = window.__brimDeckMusicV1;
      if (older && typeof older.detach === 'function')
        for (const name of Object.getOwnPropertyNames(window)) if (/^__brimDeckMediaPush(_[a-f0-9]{32})?$/.test(name)) older.detach(name);
      const outputs = new Set();
      let position = null, positionTrack = '', latest = null, lastWire = '', lastProgress = 0;
      const modes = ['playOrder', 'playCycle', 'playOneCycle', 'playRandom'];
      const clean = value => typeof value === 'string' ? value.slice(0, 2048) : '';
      const songId = p => String((p.trackFileType === 'local' && p.onlineResourceId) || p.resourceTrackId || p.curPlaying?.resourceId || '');
      const info = () => {
        const s = store.getState(), p = s.playing, t = p.curTrack || p.curPlaying?.track || {}, id = songId(p);
        const rawDuration = t.duration ?? t.dt;
        const duration = Number.isFinite(rawDuration) && rawDuration > 0 ? rawDuration : null;
        const title = clean(p.resourceName || t.name);
        if (id !== positionTrack) { position = null; positionTrack = id; }
        const artist = (p.resourceArtists || t.artists || t.ar || []).map(a => clean(a.name)).filter(Boolean).join(' / ');
        const album = t.album || t.al || {};
        const state = p.playingState === 2 ? 'playing' : p.playingState === 1 ? 'paused' : 'unknown';
        // The client's own liked songs, keyed by song id. Liking needs a signed-in, online client; podcasts are not songs.
        const likes = s['async:hostResource']?.likeTracksMap;
        const canLike = !!title && p.resourceType === 'track' && /^\d+$/.test(id) && !!likes && typeof likes === 'object' && !!s.host?.uid && s.app?.isOnline === true;
        return { title, artist, album: clean(album.albumName || album.name), songId: /^\d+$/.test(id) ? id : '', canLike, liked: canLike && likes[id] === true,
          coverUrl: clean(p.resourceCoverUrl || album.picUrl), durationMs: duration, positionMs: position,
          state, mode: modes.includes(p.playingMode) ? p.playingMode : '',
          previousRestricted: p.playingMode === 'playFm', canControl: !!title, canSeek: !!title && !!duration, canMode: modes.includes(p.playingMode) };
      };
      const publish = progress => {
        latest = info();
        if (outputs.size === 0) return;
        const now = performance.now();
        if (progress && now - lastProgress < 900) return;
        const wire = JSON.stringify(latest);
        if (wire === lastWire) return;
        lastWire = wire; lastProgress = now;
        for (const output of outputs) {
          try { if (typeof window[output] === 'function') window[output](wire); else outputs.delete(output); }
          catch { outputs.delete(output); }
        }
      };
      const validEvent = event => {
        if (!event || typeof event.playId !== 'string') return false;
        const p = store.getState().playing, id = songId(p);
        return !!id && event.playId.split('_')[0] === id;
      };
      const onProgress = event => {
        if (!validEvent(event) || !Number.isFinite(event.current) || event.current < 0) return;
        positionTrack = songId(store.getState().playing); position = event.current * 1000; publish(true);
      };
      const onSeek = event => {
        if (!validEvent(event) || !Number.isFinite(event.position) || event.position < 0 || (event.code !== undefined && event.code !== 0)) return;
        positionTrack = songId(store.getState().playing); position = event.position * 1000; publish(false);
      };
      // Read only the saved position of the current paused track; never inspect other storage keys.
      // After a restart the client reports the paused state before the restored track id, so this
      // runs on every state change and reads once per track as soon as both are known.
      let savedTrack = '';
      const restoreSaved = () => {
        const p = store.getState().playing, id = songId(p);
        if (!storage || p.playingState !== 1 || !id || savedTrack === id || (position !== null && positionTrack === id)) return;
        savedTrack = id;
        Promise.resolve(storage.lastPlaying.get()).then(saved => {
          const p = store.getState().playing, id = songId(p);
          if (p.playingState !== 1 || String(saved?.trackId) !== id || !Number.isFinite(saved.current) || saved.current < 0 || (position !== null && positionTrack === id)) return;
          positionTrack = id; position = saved.current * 1000; publish(false);
        }).catch(() => {});
      };
      const onState = () => { restoreSaved(); publish(false); };
      const unsubscribe = store.subscribe(onState);
      if (player) {
        player.subscribePlayStatus({ type: 'playprogress', callback: onProgress });
        player.subscribePlayStatus({ type: 'seek', callback: onSeek });
        player.subscribePlayStatus({ type: 'playstate', callback: onState });
      }
      const bridge = {
        version: 2,
        attach(name) {
          if (outputs.size >= 64 && !outputs.has(name)) return { ready: false };
          outputs.add(name); lastWire = ''; publish(false); return { ready: true, snapshot: latest };
        },
        detach(name) { outputs.delete(name); },
        command(request) {
          const p = store.getState().playing;
          if (!p || !info().title) return { dispatched: false };
          let type, payload = {};
          switch (request.command) {
            case 'toggle':
              if (![1, 2].includes(p.playingState)) return { dispatched: false };
              type = p.playingState === 2 ? 'playing/pause' : 'playing/resume'; break;
            case 'next': case 'previous':
              if (request.command === 'previous' && p.playingMode === 'playFm') return { dispatched: false };
              type = 'playingList/jump2Track'; payload = { flag: request.command === 'next' ? 1 : -1, type: 'call', triggerScene: 'desktopLyric' }; break;
            case 'seek': {
              const s = info();
              if (String(request.songId) !== s.songId || !Number.isFinite(request.seconds) || request.seconds < 0 || !s.durationMs || request.seconds * 1000 > s.durationMs) return { dispatched: false };
              type = 'playing/setPlayingPosition'; payload = { duration: request.seconds }; break;
            }
            case 'like': {
              // The same action as the client's own liking hotkey and mini player heart: it toggles the current song.
              const s = info();
              if (!s.canLike || String(request.songId) !== s.songId) return { dispatched: false };
              type = 'async:hostResource/setLikeCurPlayingTrack'; break;
            }
            case 'mode':
              if (!modes.includes(p.playingMode) || !modes.includes(request.mode)) return { dispatched: false };
              type = 'playing/switchPlayingMode'; payload = { playingMode: request.mode, triggerScene: 'sysTray' }; break;
            case 'shuffle':
              if (!modes.includes(p.playingMode)) return { dispatched: false };
              type = 'playing/switchPlayingMode'; payload = { playingMode: p.playingMode === 'playRandom' ? 'playCycle' : 'playRandom', triggerScene: 'sysTray' }; break;
            case 'repeat':
              if (!modes.includes(p.playingMode)) return { dispatched: false };
              type = 'playing/switchPlayingMode'; payload = { playingMode: p.playingMode === 'playCycle' ? 'playOneCycle' : p.playingMode === 'playOneCycle' ? 'playOrder' : 'playCycle', triggerScene: 'sysTray' }; break;
            default: return { dispatched: false };
          }
          // Return only whether dispatch occurred; the resulting event confirms actual player state.
          store.dispatch({ type, payload });
          return { dispatched: true };
        }
      };
      window.__brimDeckMusicV2 = withLeases(bridge);
      restoreSaved();
      return bridge.attach(binding);
    })()
    """;
}
