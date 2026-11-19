"use strict";
const HOST = "com.cozy_popout.companion";
const CHANNEL = "cozy-native-v3";
let port = null;
let nativeReady = false;
let source = null;
let lastState = null;
let pollTimer = null;
let reading = false;
let pendingCommands = [];
let applying = false;

async function readTab(tabId) {
  try {
    const state = await chrome.tabs.sendMessage(tabId,{channel:CHANNEL,action:"state"});
    return state?.available ? {tabId,state} : null;
  } catch { return null; }
}
async function poll() {
  if (!port || reading || applying) return;
  reading = true;
  const currentPort = port;
  try {
    const tabs = await chrome.tabs.query({});
    const active = (await chrome.tabs.query({active:true,lastFocusedWindow:true}))[0]?.id;
    const results=(await Promise.all(tabs.filter(t=>Number.isInteger(t.id)).map(t=>readTab(t.id)))).filter(Boolean);
    const selected=results.find(r=>r.state.pip) || results.find(r=>r.tabId===source) ||
      results.find(r=>r.tabId===active) || (results.length===1 ? results[0] : null);
    if (port !== currentPort || applying) return;
    source=selected?.tabId ?? null;
    lastState=selected?.state ?? null;
    currentPort.postMessage({type:"state", ...(lastState || {available:false}), bridgeVersion:1});
  } catch { if (port===currentPort) currentPort.postMessage({type:"state",available:false}); }
  finally { reading=false; }
}
function validCommand(message) {
  if (message?.type!=="command" || typeof message.key!=="string" || !message.key || message.key.length>200) return false;
  switch (message.action) {
    case "play": case "mute": case "next": case "previous": return true;
    case "skip": return message.value===10 || message.value===-10;
    case "seek": return Number.isFinite(message.value) && message.value>=0 && message.value<=1e10;
    case "volume": return Number.isFinite(message.value) && message.value>=0 && message.value<=1;
    case "speed": return [.25,.5,.75,1,1.25,1.5,1.75,2].includes(message.value);
    default: return false;
  }
}
async function applyCommand(message, currentPort) {
  if (port!==currentPort || !validCommand(message)) return;
  const target=source;
  if (!Number.isInteger(target) || !lastState || message.key!==lastState.key) {
    currentPort.postMessage({type:"error",message:"The video changed. Try the control again."});
    return;
  }
  try {
    const reply=await chrome.tabs.sendMessage(target,{channel:CHANNEL,action:message.action,value:message.value,key:message.key});
    if (port!==currentPort) return;
    if (reply?.error) currentPort.postMessage({type:"error",message:String(reply.error).slice(0,240)});
    if (reply?.state && source===target) {
      lastState=reply.state;
      currentPort.postMessage({type:"state",...lastState});
    }
  } catch { if(port===currentPort) currentPort.postMessage({type:"error",message:"Refresh the YouTube source tab."}); }
}
async function enqueue(message,currentPort) {
  if (!validCommand(message)) return;
  const tail=pendingCommands[pendingCommands.length-1];
  // Replace only adjacent slider samples: keep the latest value without a drag backlog.
  if (["volume","seek"].includes(message.action) && tail?.message.action===message.action &&
      tail.message.key===message.key && tail.currentPort===currentPort) tail.message=message;
  else if(pendingCommands.length<32) pendingCommands.push({message,currentPort});
  if(applying) return;
  applying=true;
  try { while(pendingCommands.length) {const item=pendingCommands.shift();await applyCommand(item.message,item.currentPort);} }
  finally {applying=false;}
}
function clearConnection(currentPort, reason) {
  if (port !== currentPort) return;
  port = null;
  nativeReady = false;
  source = null;
  lastState = null;
  pendingCommands = [];
  clearInterval(pollTimer);
  pollTimer = null;
  if (typeof onNativeDisconnect === "function") onNativeDisconnect(reason);
}
function connectNative() {
  if(port) return {connected:true};
  try {
    const currentPort=chrome.runtime.connectNative(HOST);
    port=currentPort;
    currentPort.onMessage.addListener(message=>{
      if(message?.type==="ready") {nativeReady=true;return;}
      if(message?.type==="refresh") { poll(); return; }
      enqueue(message,currentPort).catch(()=>{});
    });
    currentPort.onDisconnect.addListener(()=>{
      // Consume expected errors (host not yet set up, app/browser closed).
      const reason=chrome.runtime.lastError?.message || "";
      clearConnection(currentPort, reason);
    });
    currentPort.postMessage({type:"state",available:false});
    poll(); pollTimer=setInterval(poll,700);
    return {connected:true};
  } catch { port=null; return {connected:false}; }
}
chrome.runtime.onMessage.addListener((message,sender,reply)=>{
  if(sender.id!==chrome.runtime.id || !["popup.html","connect.html"].some(file=>sender.url===chrome.runtime.getURL(file))) return;
  if(sender.tab && sender.url!==chrome.runtime.getURL("connect.html")) return;
  if(message?.type==="cozy-desktop-focus") {
    if (!port && typeof connect === "function") void connect();
    chrome.tabs.getCurrent().then(tab=>reply({tabId:tab?.id})).catch(()=>reply(null));
    return true;
  }
  if(message?.type==="cozy-desktop-status") {reply({connected:nativeReady,connecting:!!port});return;}
  if(message?.type==="cozy-desktop-connect") { reply(connectNative()); }
});
// Runs only in the visible connection tab. There is no background/service worker.
