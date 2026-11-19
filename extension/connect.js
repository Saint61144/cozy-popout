"use strict";
const heading=document.getElementById("heading"),message=document.getElementById("message"),retry=document.getElementById("retry");
const details=document.getElementById("details");
let attempt=0;
async function connect(){
 const current=++attempt;
 retry.hidden=true;
 details.hidden=true;
 heading.textContent="Connecting…";
 message.textContent="";
 try{
  connectNative();
  for(let i=0;i<30 && current===attempt;i++){
   const state={connected:nativeReady,connecting:!!port};
   if(state?.connected){heading.textContent="Connected";message.textContent="Leave this tab open and click Reiner on your desktop.";return;}
   if(!state?.connecting)break;
   await new Promise(resolve=>setTimeout(resolve,400));
  }
 }catch{}
 if(current!==attempt)return;
 if (port) {
  const timedOutPort = port;
  clearConnection(timedOutPort, "The desktop app did not answer in time.");
  timedOutPort.disconnect();
  return;
 }
 onNativeDisconnect();
}
function onNativeDisconnect(reason=""){
 attempt++;
 heading.textContent="Connect desktop controls";
 message.textContent=/not found|forbidden|access/i.test(reason)
   ? "Open Cozy Desktop and press Connect for this browser."
   : "Open Cozy Desktop, then press Connect here.";
 details.textContent=reason;
 details.hidden=!reason;
 retry.hidden=false;
}
window.addEventListener("pagehide",()=>{if(port)port.disconnect();});
retry.addEventListener("click",connect);connect();
