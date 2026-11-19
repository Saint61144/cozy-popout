(() => {
  "use strict";
  // YouTube owns the sound preference. Update its player, not only the video
  // element which YouTube can later reset from its own retained settings.
  if (window.top !== window) return;
  document.addEventListener("cozy:sound-request:v1", event => {
    let request;
    try { request = JSON.parse(event.detail); } catch { return; }
    if (!Number.isSafeInteger(request?.id) || request.id < 1) return;
    const player = document.getElementById("movie_player");
    let ok = false;
    try {
      if (request.action === "volume" && Number.isFinite(request.value) &&
          request.value >= 0 && request.value <= 1 &&
          typeof player?.setVolume === "function" && typeof player?.mute === "function" &&
          typeof player?.unMute === "function") {
        player.setVolume(Math.round(request.value * 100));
        if (request.value === 0) player.mute(); else player.unMute();
        ok = true;
      } else if (request.action === "mute" && typeof request.value === "boolean" &&
                 typeof player?.mute === "function" && typeof player?.unMute === "function") {
        if (request.value) player.mute(); else player.unMute();
        ok = true;
      }
    } catch { ok = false; }
    document.dispatchEvent(new CustomEvent("cozy:sound-response:v1", {
      detail: JSON.stringify({ id: request.id, ok })
    }));
  });
})();
