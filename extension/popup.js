"use strict";
const $ = id => document.getElementById(id);
let source = null;
let state = null;
let polling = false;
let busy = 0;
const editing = new Set();
const channel = "cozy-native-v3";
async function connectDesktop() {
  try {
    const existing = await chrome.runtime.sendMessage({type:"cozy-desktop-focus"}).catch(() => null);
    if (Number.isInteger(existing?.tabId)) {
      await chrome.tabs.update(existing.tabId, {active:true});
      return;
    }
    await chrome.tabs.create({url:chrome.runtime.getURL("connect.html")});
  } catch {
    $("status").textContent = "Couldn’t open desktop connection.";
  }
}
$("connect").addEventListener("click",connectDesktop);
async function readTab(id) {
  try {
    const snapshot = await chrome.tabs.sendMessage(id, { channel, action: "state" });
    return snapshot?.available ? { tabId: id, state: snapshot } : null;
  } catch { return null; }
}
async function resolveSource() {
  // Browser-menu controls talk directly to content scripts.
  // IDs and active flags need no tabs permission; URLs/history are never read.
  const tabs = await chrome.tabs.query({});
  const activeTabs = await chrome.tabs.query({ active: true, currentWindow: true });
  const activeId = activeTabs[0]?.id;
  const results = (await Promise.all(tabs.filter(t => Number.isInteger(t.id)).map(t => readTab(t.id))))
    .filter(Boolean);
  return results.find(r => r.state.pip) || results.find(r => r.tabId === activeId) ||
    (results.length === 1 ? results[0] : null);
}
async function communicate(request) {
  if (request.action === "resolve") {
    return await resolveSource() || { error: "Open a YouTube video first." };
  }
  if (!Number.isInteger(request.tabId)) return { error: "Choose the video again." };
  try {
    const response = await chrome.tabs.sendMessage(request.tabId, {
      channel, action: request.action, key: request.key, value: request.value
    });
    return request.action === "state" ? { state: response } : response;
  } catch { return { error: "The source tab closed or reloaded. Reopen this menu." }; }
}
function format(seconds) {
  if (!Number.isFinite(seconds) || seconds < 0) return "0:00";
  seconds = Math.floor(seconds);
  const h = Math.floor(seconds / 3600);
  const m = Math.floor(seconds / 60) % 60;
  return (h ? `${h}:${String(m).padStart(2, "0")}` : String(m)) + `:${String(seconds % 60).padStart(2, "0")}`;
}
function render(next) {
  if (!next?.available) {
    state = null;
    $("remote").disabled = true;
    $("remote").hidden = true;
    $("title").textContent = "Open a YouTube video";
    return;
  }
  if (state && state.key !== next.key) editing.clear();
  state = next;
  $("remote").disabled = false;
  $("remote").hidden = false;
  $("title").textContent = next.title || "YouTube video";
  $("title").title = next.title || "YouTube video";
  $("play").textContent = next.paused ? "▶" : "Ⅱ";
  $("play").setAttribute("aria-label", next.paused ? "Play" : "Pause");
  $("mute").textContent = next.muted ? "Unmute" : "Mute";
  $("mute").setAttribute("aria-label", next.muted ? "Unmute" : "Mute");
  $("mute").setAttribute("aria-pressed", String(next.muted));
  $("next").disabled = !next.canNext;
  $("previous").disabled = !next.canPrevious;
  $("seek").disabled = $("back").disabled = $("forward").disabled = !next.canSeek;
  if (!editing.has("seek")) {
    $("seek").min = String(next.start);
    $("seek").max = String(Math.max(next.start + 0.1, next.end));
    $("seek").value = String(next.time);
  }
  $("seek").setAttribute("aria-valuetext", format(next.time));
  $("time").textContent = `${format(next.time)} / ${next.duration === null ? "LIVE" : format(next.duration)}`;
  if (!editing.has("volume")) $("volume").value = String(next.volume);
  if (!editing.has("speed")) $("speed").value = String(next.rate);
}
async function send(action, value, videoKey = state?.key) {
  if (source === null || !state) return;
  busy++;
  try {
    const reply = await communicate({ tabId: source, action, value, key: videoKey });
    $("status").textContent = reply?.error || "";
    if (reply?.state) render(reply.state);
  } catch {
    $("status").textContent = "Cannot reach YouTube. Reopen this menu.";
  } finally { busy--; }
}
$("play").addEventListener("click", () => send("play"));
$("back").addEventListener("click", () => send("skip", -10));
$("forward").addEventListener("click", () => send("skip", 10));
$("next").addEventListener("click", () => send("next"));
$("previous").addEventListener("click", () => send("previous"));
$("mute").addEventListener("click", () => send("mute"));
// Polling never overwrites a control while it has keyboard focus or pointer capture.
// Preserve the media key from the start of a gesture to reject stale seeks on navigation.
for (const id of ["seek", "volume", "speed"]) {
  const input = $(id);
  let gestureKey = null;
  const begin = () => {
    if (!editing.has(id)) gestureKey = state?.key;
    editing.add(id);
  };
  input.addEventListener("focus", begin);
  input.addEventListener("pointerdown", event => {
    begin();
    if (id !== "speed") input.setPointerCapture(event.pointerId);
  });
  input.addEventListener("blur", () => { editing.delete(id); gestureKey = null; });
  input.addEventListener("lostpointercapture", () => {
    if (document.activeElement !== input) { editing.delete(id); gestureKey = null; }
  });
  input.addEventListener("change", () => {
    // Volume is already applied by each input event, including keyboard steps.
    if (id !== "volume") send(id === "seek" ? "seek" : "speed", Number(input.value), gestureKey || state?.key);
    gestureKey = state?.key;
  });
  if (id === "volume") input.addEventListener("input", () => {
    send("volume", Number(input.value), gestureKey || state?.key);
  });
  if (id === "seek") input.addEventListener("input", () => {
    $("time").textContent = `${format(Number(input.value))} / ${state?.duration === null ? "LIVE" : format(state?.duration)}`;
  });
}
async function poll() {
  if (source === null || polling || busy) return;
  polling = true;
  try {
    const reply = await communicate({ tabId: source, action: "state" });
    if (!busy && reply?.state) render(reply.state);
    if (reply?.error) { $("status").textContent = reply.error; render(null); }
  } catch { $("status").textContent = "Source disconnected. Reopen this menu."; render(null); }
  finally { polling = false; }
}
(async () => {
  try {
    const reply = await communicate({ action: "resolve" });
    if (reply?.error) { render(null); return; }
    if (!Number.isInteger(reply?.tabId)) throw new Error("No source");
    source = reply.tabId;
    render(reply.state);
    setInterval(poll, 800);
  } catch { render(null); $("status").textContent = "If YouTube is open, refresh the tab."; }
})();
