const $=s=>document.querySelector(s), $$=s=>[...document.querySelectorAll(s)];
let snapshot=null;
const titles={overview:["节点总览","连接、流量和服务状态"],clients:["客户端","在线设备与连接信息"],tunnels:["隧道分配","服务端端口租约与限速"],config:["服务配置","安全编辑并应用 frps.toml"],security:["安全","面板访问凭据"]};

async function api(path, options={}) {
  const response=await fetch(path,{headers:{"Content-Type":"application/json",...(options.headers||{})},...options});
  if(response.status===401){showLogin();throw new Error("登录已失效");}
  const text=await response.text(); let body={};
  try{body=text?JSON.parse(text):{}}catch{body={error:text}}
  if(!response.ok)throw new Error(body.error||`请求失败 (${response.status})`);
  return body;
}
function showLogin(){ $("#app").classList.add("hidden");$("#login").classList.remove("hidden"); }
function showApp(){ $("#login").classList.add("hidden");$("#app").classList.remove("hidden"); }
function toast(message){const box=$("#toast");box.textContent=message;box.classList.add("show");setTimeout(()=>box.classList.remove("show"),2200)}
function pick(object,...keys){for(const key of keys){if(object&&object[key]!=null)return object[key]}return 0}
function arrayFrom(value){if(Array.isArray(value))return value;if(Array.isArray(value?.clients))return value.clients;if(Array.isArray(value?.proxies))return value.proxies;return []}
function fmtBytes(value){let n=Number(value||0);for(const unit of ["B","KB","MB","GB","TB"]){if(n<1024||unit==="TB")return `${n<10&&unit!=="B"?n.toFixed(1):Math.round(n)} ${unit}`;n/=1024}}
function escapeHtml(value){return String(value??"").replace(/[&<>"']/g,ch=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[ch]))}

async function refresh(){
  try{
    snapshot=await api("/api/overview");
    render(snapshot);
    $("#updated").textContent=new Date().toLocaleTimeString()+" 更新";
  }catch(error){toast(error.message)}
}
function render(data){
  $("#node-health").className=`health ${data.reachable?"online":"offline"}`;
  $("#node-health").innerHTML=`<i></i>${data.reachable?"节点在线":"节点离线"}`;
  const clients=arrayFrom(data.clients);
  const proxyGroups=Object.values(data.proxies||{}).flatMap(arrayFrom);
  const server=data.server||{};
  $("#metric-clients").textContent=pick(server,"clientCounts","client_count")||clients.filter(x=>x.status!=="offline").length;
  $("#metric-proxies").textContent=pick(server,"proxyCount","proxy_count")||proxyGroups.length||data.allocations?.length||0;
  $("#metric-in").textContent=fmtBytes(pick(server,"totalTrafficIn","total_traffic_in"));
  $("#metric-out").textContent=fmtBytes(pick(server,"totalTrafficOut","total_traffic_out"));
  $("#server-info").innerHTML=[
    ["运行状态",data.reachable?"正常":"不可用"],["frps 版本",pick(server,"version")||"未知"],
    ["绑定端口",pick(server,"bindPort","bind_port")||"7000"],["运行时间",pick(server,"uptime")||"等待数据"]
  ].map(([a,b])=>`<div class="detail"><span>${a}</span><strong>${escapeHtml(b)}</strong></div>`).join("");
  $("#audit-list").innerHTML=(data.audit||[]).map(x=>`<div class="activity"><b>${escapeHtml(x.action)}</b><span>${new Date(x.time).toLocaleString()} · ${escapeHtml(x.detail)}</span></div>`).join("")||`<div class="empty">暂无操作记录</div>`;
  $("#clients-body").innerHTML=clients.map(x=>`<tr><td><strong>${escapeHtml(pick(x,"clientID","clientId","id","user")||"未命名")}</strong></td><td>${escapeHtml(pick(x,"clientAddress","client_address","address")||"—")}</td><td><span class="tag ${x.status==="offline"?"off":""}">${escapeHtml(x.status||"online")}</span></td><td>${escapeHtml(x.protocol||x.version||"—")}</td><td>${escapeHtml(pick(x,"connectTime","connect_time","lastSeen")||"—")}</td></tr>`).join("")||emptyRow(5,"暂无已连接客户端");
  $("#allocations-body").innerHTML=(data.allocations||[]).map(x=>`<tr><td><strong>${escapeHtml(x.proxyName)}</strong></td><td>${escapeHtml(x.clientId)}</td><td>${escapeHtml(x.proxyType.toUpperCase())}</td><td>${x.remotePort}</td><td>${escapeHtml(x.bandwidthLimit||"不限速")}</td><td><span class="tag">已锁定</span></td><td><button class="danger release" data-id="${x.id}">释放</button></td></tr>`).join("")||emptyRow(7,"暂无端口租约");
  $$(".release").forEach(button=>button.onclick=async()=>{if(!confirm("释放该端口租约？客户端下次连接将被拒绝。"))return;try{await api(`/api/allocations/${button.dataset.id}`,{method:"DELETE"});toast("租约已释放");refresh()}catch(e){toast(e.message)}});
}
function emptyRow(columns,text){return `<tr><td class="empty" colspan="${columns}">${text}</td></tr>`}
async function loadConfig(){try{const result=await api("/api/config");$("#config-editor").value=result.content||""}catch(e){toast(e.message)}}

$("#login-form").onsubmit=async event=>{event.preventDefault();$("#login-error").textContent="";try{await api("/api/auth/login",{method:"POST",body:JSON.stringify({password:$("#password").value})});$("#password").value="";showApp();refresh()}catch(error){$("#login-error").textContent=error.message}};
$("#logout").onclick=async()=>{await api("/api/auth/logout",{method:"POST"});showLogin()};
$("#refresh").onclick=refresh;
$$(".nav").forEach(button=>button.onclick=()=>{$$(".nav,.page").forEach(x=>x.classList.remove("active"));button.classList.add("active");const page=button.dataset.page;$("#page-"+page).classList.add("active");[$("#page-title").textContent,$("#page-subtitle").textContent]=titles[page];if(page==="config")loadConfig();});
$$("[data-service]").forEach(button=>button.onclick=async()=>{try{const result=await api(`/api/service/${button.dataset.service}`,{method:"POST"});toast(result.message);setTimeout(refresh,900)}catch(e){toast(e.message)}});
$("#save-config").onclick=async()=>{const status=$("#config-status");status.textContent="正在校验...";try{const result=await api("/api/config",{method:"PUT",body:JSON.stringify({content:$("#config-editor").value,restart:$("#restart-after-save").checked})});status.textContent=result.message;toast(result.message);refresh()}catch(e){status.textContent=e.message;toast(e.message)}};
$("#password-form").onsubmit=async event=>{event.preventDefault();try{await api("/api/admin/password",{method:"POST",body:JSON.stringify({currentPassword:$("#current-password").value,newPassword:$("#new-password").value})});$("#password-status").textContent="密码已更新";event.target.reset()}catch(e){$("#password-status").textContent=e.message}};

(async()=>{try{const session=await api("/api/session");if(session.authenticated){showApp();await refresh()}else showLogin()}catch{showLogin()}})();
setInterval(()=>{if(!$("#app").classList.contains("hidden"))refresh()},10000);
