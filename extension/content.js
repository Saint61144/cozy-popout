(() => {
  "use strict";
  // Native PiP displays the original video. Never move, clone, mute, or restyle it.
  if (window.top !== window || document.getElementById("cozy-native-launcher")) return;
  const host = document.createElement("div");
  host.id = "cozy-native-launcher";
  host.style.cssText = "position:fixed;right:22px;bottom:22px;z-index:2147483647";
  const root = host.attachShadow({ mode: "closed" });
  const style = document.createElement("style");
  style.textContent = `
    :host{all:initial} :host([hidden]){display:none!important} *{box-sizing:border-box}
    button{display:flex;align-items:center;gap:9px;padding:7px 16px 7px 7px;
      border:1px solid #a77f62;border-radius:999px;background:#ead8bd;color:#30221c;
      font:600 14px/1.4 system-ui,sans-serif;box-shadow:0 4px 20px #0006;cursor:pointer}
    button:hover{background:#f7ecdc;color:#752c2c}button:disabled{cursor:wait;opacity:.7}
    button:focus-visible{outline:3px solid #8a3030;outline-offset:3px}
    img{width:34px;height:34px;object-fit:contain;pointer-events:none}
    p{max-width:290px;padding:12px 14px;border:1px solid #a77f62;border-radius:12px;
      background:#30221c;color:#fff7eb;font:13px/1.5 system-ui,sans-serif}
    p:empty{display:none}
  `;
  const launch = document.createElement("button");
  launch.type = "button";
  launch.title = "Float video";
  const mascot = document.createElement("img");
  mascot.src = chrome.runtime.getURL("reiner48.png");
  mascot.alt = "";
  const label = document.createElement("span");
  const message = document.createElement("p");
  message.setAttribute("role", "status");
  launch.append(mascot, label);
  root.append(style, message, launch);
  document.documentElement.append(host);

  let video = null;
  let generation = 0;
  let opening = false;
  let stopped = false;
  let noticeTimer;
  let refreshTimer;
  let navigationPendingUntil = 0;
  let navigationKey = "";
  let soundRequestId = 0;
  function setPlayerSound(action, value) {
    return new Promise(resolve => {
      const id = ++soundRequestId;
      const finish = ok => {
        clearTimeout(timeout);
        document.removeEventListener("cozy:sound-response:v1", response);
        resolve(ok);
      };
      const response = event => {
        try {
          const data = JSON.parse(event.detail);
          if (data.id === id) finish(data.ok === true);
        } catch { /* Ignore unrelated page events. */ }
      };
      const timeout = setTimeout(() => finish(false), 1500);
      document.addEventListener("cozy:sound-response:v1", response);
      document.dispatchEvent(new CustomEvent("cozy:sound-request:v1", {
        detail: JSON.stringify({ id, action, value })
      }));
    });
  }
  const mediaEvents = ["loadedmetadata", "emptied", "enterpictureinpicture", "leavepictureinpicture"];
  const isWatch = () => location.pathname === "/watch" && !!new URL(location.href).searchParams.get("v");
  const findVideo = () => isWatch() ? document.querySelector("#movie_player video.html5-main-video") : null;
  const key = () => `${generation}:${new URL(location.href).searchParams.get("v") || ""}`;
  const inPip = () => !!video && document.pictureInPictureElement === video;

  function notice(text) {
    clearTimeout(noticeTimer);
    message.textContent = text;
    noticeTimer = setTimeout(() => { message.textContent = ""; }, 9000);
  }
  function navigationButton(direction) {
    const button = document.querySelector(`#movie_player .ytp-${direction === "previous" ? "prev" : "next"}-button`);
    if (!button || button.disabled || button.getAttribute("aria-disabled") === "true" ||
        button.hidden || getComputedStyle(button).display === "none" ||
        getComputedStyle(button).visibility === "hidden") return null;
    // No speculative navigation to recommendations or arbitrary links.
    const href = button.getAttribute("href");
    if (href) {
      try {
        const target = new URL(href, location.href);
        if (target.origin !== location.origin || target.pathname !== "/watch" ||
            !target.searchParams.get("v") ||
            target.searchParams.get("v") === new URL(location.href).searchParams.get("v")) return null;
      } catch { return null; }
    }
    return button;
  }
  function refresh() {
    if (stopped) return;
    const candidate = findVideo();
    if (candidate !== video) {
      const previous = video;
      const wasFloating = previous && document.pictureInPictureElement === previous;
      if (previous) for (const type of mediaEvents) previous.removeEventListener(type, onMedia);
      video = candidate;
      generation++;
      if (video) for (const type of mediaEvents) video.addEventListener(type, onMedia);
      if (wasFloating) {
        // A replaced/disconnected video can no longer be controlled reliably.
        // Do not auto-reopen after a user closes PiP or steal another tab's PiP.
        document.exitPictureInPicture().catch(() => {});
        notice("YouTube replaced the video. Click Float video to open the new one.");
      }
    }
    host.hidden = !isWatch() || !video || !!document.fullscreenElement;
    const caption = inPip() ? "Return video" : "Float video";
    // Avoid creating child-list mutations on every refresh (the observer watches this host too).
    if (label.textContent !== caption) label.textContent = caption;
    launch.setAttribute("aria-label", label.textContent);
    launch.disabled = opening;
    if (navigationKey && navigationKey !== key()) {
      navigationPendingUntil = 0;
      navigationKey = "";
    }
  }
  function onMedia(event) {
    if (event.type === "leavepictureinpicture") {
      // Playback state, current time, volume and speed are intentionally untouched.
      notice("Returned to YouTube. Playback keeps its current state.");
    }
    refresh();
  }
  async function togglePip() {
    refresh();
    if (opening) return;
    if (!video) return notice("Open a normal YouTube watch video first.");
    if (!document.pictureInPictureEnabled || typeof video.requestPictureInPicture !== "function") {
      return notice("Native video Picture-in-Picture is unavailable or disabled in this browser.");
    }
    const target = video;
    if (!inPip() && (!target.readyState || !target.videoWidth)) {
      return notice("Wait for the video picture to load, then try again.");
    }
    if (!inPip() && target.disablePictureInPicture) {
      return notice("This video has disabled native Picture-in-Picture.");
    }
    opening = true;
    launch.disabled = true;
    try {
      if (document.pictureInPictureElement === target) {
        await document.exitPictureInPicture();
      } else {
        // Called synchronously from the page button's trusted click: no await before request.
        await target.requestPictureInPicture();
        refresh();
        if (target !== video && document.pictureInPictureElement === target) {
          await document.exitPictureInPicture();
          notice("The video changed while opening. Please try again.");
        }
      }
    } catch (error) {
      const explanations = {
        NotAllowedError: "The browser blocked PiP. Click Float video directly on the YouTube page and try again.",
        InvalidStateError: "This video is not ready for PiP, or YouTube has disabled it.",
        NotSupportedError: "Native video PiP is not supported for this video.",
        SecurityError: "The page's permissions policy does not allow native PiP."
      };
      notice(explanations[error.name] || "Could not change PiP. Refresh YouTube and try again.");
    } finally {
      opening = false;
      refresh();
    }
  }
  function seekTo(value) {
    if (!Number.isFinite(value) || !video.seekable.length) return false;
    // Choose the nearest valid range; supports live DVR and disjoint buffered timelines.
    let nearest = null;
    let distance = Infinity;
    for (let i = 0; i < video.seekable.length; i++) {
      const start = video.seekable.start(i);
      const end = Math.max(start, video.seekable.end(i) - 0.05);
      const candidate = Math.min(end, Math.max(start, value));
      if (Math.abs(candidate - value) < distance) {
        nearest = candidate;
        distance = Math.abs(candidate - value);
      }
    }
    if (nearest === null) return false;
    video.currentTime = nearest;
    return true;
  }
  function snapshot() {
    refresh();
    if (!video) return { available: false };
    const seekable = video.seekable;
    return {
      available: true, key: key(), pip: inPip(), paused: video.paused,
      muted: video.muted, volume: video.volume, rate: video.playbackRate,
      time: Number.isFinite(video.currentTime) ? video.currentTime : 0,
      duration: Number.isFinite(video.duration) ? video.duration : null,
      start: seekable.length ? seekable.start(0) : 0,
      end: seekable.length ? seekable.end(seekable.length - 1) : 0,
      canSeek: seekable.length > 0,
      canNext: !!navigationButton("next") && Date.now() >= navigationPendingUntil,
      canPrevious: !!navigationButton("previous") && Date.now() >= navigationPendingUntil,
      title: (document.querySelector("h1.ytd-watch-metadata")?.textContent ||
        document.title.replace(/ - YouTube$/, "")).trim().slice(0, 180)
    };
  }
  async function command(request) {
    refresh();
    if (!video) return { error: "No watch video is available. Refresh the YouTube tab." };
    if (request.key !== key()) return { error: "The video changed. Controls have been refreshed; try again.", state: snapshot() };
    try {
      switch (request.action) {
        case "play": if (video.paused) await video.play(); else video.pause(); break;
        case "skip":
          if (![10, -10].includes(request.value)) throw new Error("Invalid skip.");
          if (!seekTo(video.currentTime + request.value)) throw new Error("This video cannot seek yet.");
          break;
        case "seek":
          if (!seekTo(request.value)) throw new Error("This video cannot seek yet.");
          break;
        case "mute":
          if (!await setPlayerSound("mute", !video.muted)) throw new Error("YouTube sound controls are not ready. Refresh its tab.");
          break;
        case "volume":
          if (!Number.isFinite(request.value) || request.value < 0 || request.value > 1) throw new Error("Invalid volume.");
          if (!await setPlayerSound("volume", request.value)) throw new Error("YouTube sound controls are not ready. Refresh its tab.");
          break;
        case "speed":
          if (![0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2].includes(request.value)) throw new Error("Invalid speed.");
          video.playbackRate = request.value;
          break;
        case "previous":
        case "next": {
          const button = navigationButton(request.action);
          if (!button || Date.now() < navigationPendingUntil) {
            throw new Error(`${request.action === "previous" ? "Previous" : "Next"} video is unavailable or still loading.`);
          }
          navigationKey = key();
          navigationPendingUntil = Date.now() + 5000;
          // YouTube normally reuses its video element. Leaving it in place gives
          // native PiP the best chance of surviving playlist/next navigation.
          button.click();
          break;
        }
        default: throw new Error("Unknown control.");
      }
      return { state: snapshot() };
    } catch (error) {
      return { error: error.message || "YouTube could not apply that control.", state: snapshot() };
    }
  }
  function onMessage(request, sender, reply) {
    if (sender.id !== chrome.runtime.id || request?.channel !== "cozy-native-v3") return;
    if (request.action === "state") { reply(snapshot()); return; }
    command(request).then(reply, () => reply({ error: "The video changed. Try again." }));
    return true;
  }
  function scheduleRefresh() {
    if (!refreshTimer) refreshTimer = setTimeout(() => { refreshTimer = null; refresh(); }, 150);
  }
  launch.addEventListener("click", togglePip);
  chrome.runtime.onMessage.addListener(onMessage);
  document.addEventListener("yt-navigate-finish", refresh);
  document.addEventListener("fullscreenchange", refresh);
  const observer = new MutationObserver(scheduleRefresh);
  observer.observe(document.documentElement, { childList: true, subtree: true });
  const poll = setInterval(refresh, 1000);
  window.addEventListener("pagehide", event => {
    if (event.persisted) return;
    stopped = true;
    observer.disconnect();
    clearInterval(poll);
    clearTimeout(refreshTimer);
    clearTimeout(noticeTimer);
    chrome.runtime.onMessage.removeListener(onMessage);
    if (video) for (const type of mediaEvents) video.removeEventListener(type, onMedia);
  });
  refresh();
})();
