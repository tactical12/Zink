using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Zink.Services
{
    public sealed class SpotifyControllerService
    {
        public sealed class TrackInfo
        {
            [JsonPropertyName("title")] public string Title { get; set; } = "";
            [JsonPropertyName("artist")] public string Artist { get; set; } = "";
            [JsonPropertyName("album")] public string Album { get; set; } = "";
            [JsonPropertyName("imageUrl")] public string ImageUrl { get; set; } = "";
            [JsonPropertyName("durationSec")] public double DurationSec { get; set; }
            [JsonPropertyName("positionSec")] public double PositionSec { get; set; }
            [JsonPropertyName("isPlaying")] public bool IsPlaying { get; set; }
            [JsonPropertyName("isLiked")] public bool IsLiked { get; set; }
        }

        public static SpotifyControllerService Instance { get; } = new();

        private CoreWebView2 _core;
        private bool _ready;
        private bool _navSubscribed;

        public event EventHandler<TrackInfo>? TrackChanged;
        public event EventHandler<bool>? PlayingChanged;

        public TrackInfo Current { get; private set; } = new();
        public bool IsPlaying { get; private set; }
        public bool IsAttached => _ready && _core != null;

        private SpotifyControllerService() { }

        public void Attach(CoreWebView2 core)
        {
            if (core is null) throw new ArgumentNullException(nameof(core));

            // ✅ Guard: if already attached to a working WebView2, do NOT override with another instance
            if (_ready && _core != null && _core != core)
                return;

            if (_core == core && _ready) return;

            if (_core != null)
                _core.WebMessageReceived -= Core_WebMessageReceived;

            _core = core;

            _core.Settings.IsWebMessageEnabled = true;
            _core.Settings.AreHostObjectsAllowed = true;
            _core.Settings.AreDefaultScriptDialogsEnabled = true;

            _core.WebMessageReceived += Core_WebMessageReceived;

            if (!_navSubscribed)
            {
                _navSubscribed = true;
                _core.NavigationCompleted += async (_, __) =>
                {
                    try { await EnsureBridgeAsync(); } catch { }
                };
                _core.DOMContentLoaded += async (_, __) =>
                {
                    try { await EnsureBridgeAsync(); } catch { }
                };
            }

            _ready = true;
            _ = EnsureBridgeAsync();
        }

        private async Task EnsureBridgeAsync()
        {
            if (_core == null) return;

            try
            {
                var uri = new Uri(_core.Source ?? "about:blank");
                if (!uri.Host.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { /* ignore */ }

            // Injected bridge (controls-first liked detection + strict matched-row fallback)
            var script = @"
(() => {
  if (window.__zinkSpotifyBridgeV6) return;
  window.__zinkSpotifyBridgeV6 = true;

  const isEl = (n) => n && n.nodeType === 1;
  function* deepWalk(node) {
    if (!node) return;
    yield node;
    if (isEl(node) && node.shadowRoot) for (const c of deepWalk(node.shadowRoot)) yield c;
    for (const child of node.childNodes || []) for (const c of deepWalk(child)) yield c;
  }
  function qsDeep(sel, root) {
    const start = root || document;
    for (const n of deepWalk(start)) {
      try { if (isEl(n)) { const r = n.querySelector(sel); if (r) return r; } } catch {}
    }
    return null;
  }
  function qsaDeep(sel, root) {
    const out = [];
    const start = root || document;
    for (const n of deepWalk(start)) {
      try { if (isEl(n)) out.push(...n.querySelectorAll(sel)); } catch {}
    }
    return out;
  }
  const txt = (el) => el ? (el.textContent || '').trim() : '';
  const norm = (s) => (s || '').toLowerCase().replace(/\s+/g, ' ').trim();

  function secondsFromMMSS(s){ if(!s) return 0; const p=s.split(':').map(Number); if(p.length===2)return p[0]*60+p[1]; if(p.length===3)return p[0]*3600+p[1]*60+p[2]; return 0; }
  function cssUrl(str){ if(!str) return ''; const m=str.match(/url\((['""]?)(.+?)\1\)/); return (m&&m[2])?m[2]:''; }
  function absUrl(u){ try { return new URL(u, location.href).toString(); } catch { return u||''; } }

  function nowPlayingRoot(){
    return qsDeep('[data-testid=""now-playing-bar""]') ||
           qsDeep('footer[role=""contentinfo""]') ||
           qsDeep('footer') || document.body;
  }

  function coverUrlFromBottom(){
    const root = nowPlayingRoot(); if(!root) return '';
    let img = root.querySelector('[data-testid=""cover-art-image""] img, img[data-testid=""cover-art-image""], .cover-art img, img') ||
              qsDeep('[data-testid=""cover-art-image""] img') || qsDeep('img[data-testid=""cover-art-image""]') || qsDeep('.cover-art img');
    if (img) { const src = img.currentSrc || img.src || img.getAttribute('src') || img.getAttribute('srcset'); if (src) return src; }
    let bgEl = root.querySelector('[data-testid=""cover-art-image""], .cover-art-image, [style*=""background-image""]') ||
               qsDeep('[data-testid=""cover-art-image""], .cover-art-image, [style*=""background-image""]');
    if (bgEl) { const bg = getComputedStyle(bgEl).backgroundImage; const u = cssUrl(bg); if (u) return u; }
    return '';
  }

  // ---- Liked detection ----

  function likedFromControls(){
    const root = nowPlayingRoot();
    if (!root) return false;

    const heart = qsDeep('button[data-testid=""control-button-heart""]', root) ||
                  qsDeep('button[aria-label*=""Like"" i]', root);
    if (heart) {
      const ac = (heart.getAttribute('aria-checked') || '').toLowerCase();
      const ap = (heart.getAttribute('aria-pressed') || '').toLowerCase();
      if (ac === 'true' || ap === 'true') return true;
      const al = (heart.getAttribute('aria-label') || '').toLowerCase();
      if (/remove from (your\s+)?liked songs/.test(al)) return true;
    }

    const add = qsDeep('button[data-testid=""add-button""]', root);
    if (add) {
      const dc = (add.getAttribute('data-checked') || '').toLowerCase();
      const ac = (add.getAttribute('aria-checked') || '').toLowerCase();
      const ap = (add.getAttribute('aria-pressed') || '').toLowerCase();
      if (dc === 'true' || ac === 'true' || ap === 'true') return true;
      const al = (add.getAttribute('aria-label') || '').toLowerCase();
      if (/remove from (your\s+)?liked songs/.test(al)) return true;
    }

    return false;
  }

  function likedFromMatchedRow(){
    const title  = txt(qsDeep('[data-testid=""context-item-info-title""], [data-testid=""nowplaying-track-link""], .track-info__name a'));
    const artist = txt(qsDeep('[data-testid=""context-item-info-artist""], [data-testid=""nowplaying-artist""], .track-info__artists'));
    const nt = norm(title), na = norm(artist);
    if (!nt) return false;

    const rows = qsaDeep('[data-testid=""tracklist-row""], [role=""row""]');
    for (const r of rows) {
      const text = norm(r.textContent);
      if (!text) continue;
      if (!text.includes(nt)) continue;
      if (na && !text.includes(na)) continue;

      const on =
         r.querySelector('button[aria-checked=""true""]') ||
         r.querySelector('button[aria-pressed=""true""]') ||
         r.querySelector('button[aria-label*=""Remove from Liked Songs"" i]') ||
         r.querySelector('button[aria-label*=""Remove from your Liked Songs"" i]') ||
         r.querySelector('[data-checked=""true""]');
      if (on) return true;
    }
    return false;
  }

  function isLiked(){ return likedFromControls() || likedFromMatchedRow(); }

  // ---- Other helpers ----
  function textDeep(sel){ const el = qsDeep(sel); return txt(el); }

  async function toDataUrl(uRaw){
    try{
      let u=(uRaw||'').trim(); if(!u) return ''; if(u.startsWith('data:image/')) return u; u = absUrl(u);
      try{ const r = await fetch(u); const b = await r.blob(); const rd = new FileReader(); const p=new Promise(res=>{rd.onload=()=>res(rd.result)}); rd.readAsDataURL(b); const d = await p; if (typeof d==='string' && d.startsWith('data:image/')) return d; }catch{}
      const img=new Image(); try{img.crossOrigin='anonymous';}catch{} img.src=u; await img.decode(); const c=document.createElement('canvas'); c.width=img.naturalWidth||300; c.height=img.naturalHeight||300; c.getContext('2d').drawImage(img,0,0); return c.toDataURL('image/png');
    }catch{ return ''; }
  }

  function isPlaying(){
    const pause = qsDeep('button[aria-label=""Pause""], button[data-testid=""control-button-playpause""][aria-label*=""Pause"" i]');
    if (pause) return true;
    const play = qsDeep('button[aria-label=""Play""], button[data-testid=""control-button-playpause""][aria-label*=""Play"" i]');
    if (play) return false;
    return false;
  }

  async function currentState(){
    const title  = textDeep('[data-testid=""context-item-info-title""], [data-testid=""nowplaying-track-link""], .track-info__name a');
    const artist = textDeep('[data-testid=""context-item-info-artist""], [data-testid=""nowplaying-artist""], .track-info__artists');
    const album  = textDeep('[data-testid=""context-item-info-subtitles""] a, [data-testid=""album-link""]');

    const rawUrl = coverUrlFromBottom();
    const imageUrl = await toDataUrl(rawUrl);

    const posTxt = textDeep('[data-testid=""playback-position""], .playback-bar__progress-time:nth-of-type(1)');
    const durTxt = textDeep('[data-testid=""playback-duration""], .playback-bar__progress-time:nth-of-type(2)');
    let positionSec = secondsFromMMSS(posTxt);
    let durationSec = secondsFromMMSS(durTxt);

    if (durationSec && durationSec < positionSec) {
      const p2 = secondsFromMMSS(textDeep('[data-testid=""playback-position""]'));
      const d2 = secondsFromMMSS(textDeep('[data-testid=""playback-duration""]'));
      positionSec = p2 || positionSec;
      durationSec = d2 || durationSec;
    }

    const payload = {
      title: title || '',
      artist: artist || '',
      album: album || '',
      imageUrl: imageUrl || '',
      durationSec: Number.isFinite(durationSec) ? durationSec : 0,
      positionSec: Number.isFinite(positionSec) ? positionSec : 0,
      isPlaying: isPlaying(),
      isLiked: isLiked()
    };
    try { window.chrome.webview.postMessage(JSON.stringify({ type: 'zink_spotify_state', payload })); } catch {}
  }

  setTimeout(currentState, 300);
  const poll = setInterval(currentState, 900);
  try {
    const obs = new MutationObserver(() => currentState());
    obs.observe(document.documentElement || document, { childList: true, subtree: true, attributes: true, characterData: true });
  } catch {}

  const delay = (ms) => new Promise(r => setTimeout(r, ms));

  async function clickLikeViaAddPopover_AddOnly() {
    const add = qsDeep('button[data-testid=""add-button""]');
    if (!add) return false;
    add.click();
    await delay(140);
    const items = qsaDeep('[role=""menuitem""], [data-testid=""context-menu-item""]');
    const hit = items.find(el => {
      const t = (el.textContent || el.getAttribute('aria-label') || '').toLowerCase();
      return /(add|save)\s+to\s+(your\s+)?liked songs/.test(t);
    });
    if (hit) { hit.click(); return true; }
    return false;
  }

  async function clickLikeViaMenu() {
    const more = qsDeep('button[data-testid=""control-button-more""]')
              || qsDeep('button[aria-label*=""More options"" i]')
              || qsDeep('button[aria-haspopup=""menu""]');
    if (!more) return false;
    more.click();
    await delay(140);

    const items = qsaDeep('[role=""menuitem""], [data-testid=""context-menu-item""]');
    const hit = items.find(el => {
      const t = (el.textContent || el.getAttribute('aria-label') || '').toLowerCase();
      return /(add|save)\s+to\s+(your\s+)?liked songs|remove\s+from\s+(your\s+)?liked songs/.test(t);
    });
    if (hit) { hit.click(); return true; }
    return false;
  }

  window.__zinkSpotifyCmd = async (cmd, arg) => {
    switch(cmd) {
      case 'playpause': {
        (qsDeep('button[aria-label=""Pause""]') ||
         qsDeep('button[aria-label=""Play""]') ||
         qsDeep('button[data-testid=""control-button-playpause""]'))?.click();
        break;
      }
      case 'next': {
        (qsDeep('button[aria-label=""Next""]') ||
         qsDeep('button[data-testid=""control-button-skip-forward""]'))?.click();
        break;
      }
      case 'prev': {
        (qsDeep('button[aria-label=""Previous""]') ||
         qsDeep('button[data-testid=""control-button-skip-back""]'))?.click();
        break;
      }
      case 'seek': {
        const audio = qsDeep('audio');
        if (audio && typeof arg === 'number') audio.currentTime = Math.max(0, arg);
        break;
      }
      case 'toggle-like': {
        let ok = false;
        const root = nowPlayingRoot();
        const btn = qsDeep('button[data-testid=""add-button""]', root) ||
                    qsDeep('button[data-testid=""control-button-heart""]', root);

        if (btn && (btn.getAttribute('data-testid') || '').toLowerCase() === 'add-button') {
          ok = await clickLikeViaAddPopover_AddOnly();
        } else if (btn) {
          const before = btn.getAttribute('aria-pressed') ?? btn.getAttribute('aria-checked') ?? '';
          btn.click();
          for (let i = 0; i < 12; i++) {
            await delay(150);
            const after = btn.getAttribute('aria-pressed') ?? btn.getAttribute('aria-checked') ?? '';
            if ((after && after !== before)) { ok = true; break; }
          }
        }

        if (!ok) {
          const rowHeart = qsDeep('[data-testid=""entity-row-heart-button""]');
          if (rowHeart) { rowHeart.click(); ok = true; }
        }

        if (!ok) ok = await clickLikeViaAddPopover_AddOnly();
        if (!ok) ok = await clickLikeViaMenu();

        await delay(220);
        await currentState();
        break;
      }
      default: { /* noop */ }
    }
    setTimeout(currentState, 350);
  };
})();
";
            try { await _core.AddScriptToExecuteOnDocumentCreatedAsync(script); } catch { }
            try { await _core.ExecuteScriptAsync(script); } catch { }
        }

        private async Task<bool> EnsureBridgeAliveAsync()
        {
            if (!IsAttached) return false;
            try
            {
                var alive = await _core.ExecuteScriptAsync("(function(){return !!window.__zinkSpotifyCmd;})()");
                if (alive != null && alive.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                await EnsureBridgeAsync();
                await Task.Delay(120);
                alive = await _core.ExecuteScriptAsync("(function(){return !!window.__zinkSpotifyCmd;})()");
                return (alive != null && alive.IndexOf("true", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var raw = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw)) return;

                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("type", out var t) ||
                    t.GetString() != "zink_spotify_state") return;

                var payload = doc.RootElement.GetProperty("payload").GetRawText();
                var info = JsonSerializer.Deserialize<TrackInfo>(payload) ?? new TrackInfo();

                Current = info;
                IsPlaying = info.IsPlaying;

                TrackChanged?.Invoke(this, info);
                PlayingChanged?.Invoke(this, info.IsPlaying);
            }
            catch
            {
                // ignore malformed messages
            }
        }

        public async Task PlayPauseAsync()
        {
            if (!IsAttached) return;
            if (!await EnsureBridgeAliveAsync()) return;
            await _core.ExecuteScriptAsync(@"window.__zinkSpotifyCmd && __zinkSpotifyCmd('playpause');");
        }

        public async Task NextAsync()
        {
            if (!IsAttached) return;
            if (!await EnsureBridgeAliveAsync()) return;
            await _core.ExecuteScriptAsync(@"window.__zinkSpotifyCmd && __zinkSpotifyCmd('next');");
        }

        public async Task PreviousAsync()
        {
            if (!IsAttached) return;
            if (!await EnsureBridgeAliveAsync()) return;
            await _core.ExecuteScriptAsync(@"window.__zinkSpotifyCmd && __zinkSpotifyCmd('prev');");
        }

        public async Task SeekToAsync(double seconds)
        {
            if (!IsAttached) return;
            if (!await EnsureBridgeAliveAsync()) return;
            var s = Math.Max(0, seconds);
            await _core.ExecuteScriptAsync($@"window.__zinkSpotifyCmd && __zinkSpotifyCmd('seek', {s.ToString(System.Globalization.CultureInfo.InvariantCulture)});");
        }

        // Keep the alias (previous typo) and the correct name — both call the same core
        public Task ToggleLiQZkeAsync() => ToggleLikeCoreAsync(); // alias
        public Task ToggleLikeAsync() => ToggleLikeCoreAsync();   // correct

        private async Task ToggleLikeCoreAsync()
        {
            if (!IsAttached) return;
            if (!await EnsureBridgeAliveAsync()) return;
            await _core.ExecuteScriptAsync(@"window.__zinkSpotifyCmd && __zinkSpotifyCmd('toggle-like');");
        }

        public async Task RefreshStateAsync()
        {
            if (!IsAttached) return;
            if (!await EnsureBridgeAliveAsync()) return;
            await _core.ExecuteScriptAsync(@"(function(){ if (window.__zinkSpotifyBridgeV6){ /* poke */ } })();");
        }
    }
}
