const nodeFlagChoices = [
  ["", "无国旗"],
  ["CN", "CN 中国"],
  ["JP", "JP 日本"],
  ["US", "US 美国"],
  ["SG", "SG 新加坡"],
  ["HK", "HK 中国香港"],
  ["KR", "KR 韩国"],
  ["DE", "DE 德国"],
  ["GB", "GB 英国"],
  ["FR", "FR 法国"]
];

function normalizedNodeName(value) {
  const raw = String(value || "").trim();
  for (const flag of ["🇨🇳", "🇯🇵", "🇺🇸", "🇸🇬", "🇭🇰", "🇰🇷", "🇩🇪", "🇬🇧", "🇫🇷"]) {
    if (raw.replace(/️/g, "").startsWith(flag)) return raw.slice(flag.length).trimStart();
  }
  return raw;
}

function inferredNodeFlag(value) {
  const raw = String(value || "").replace(/️/g, "").trimStart();
  return raw.startsWith("🇨🇳") ? "CN" : raw.startsWith("🇯🇵") ? "JP" : raw.startsWith("🇺🇸") ? "US"
    : raw.startsWith("🇸🇬") ? "SG" : raw.startsWith("🇭🇰") ? "HK" : raw.startsWith("🇰🇷") ? "KR"
      : raw.startsWith("🇩🇪") ? "DE" : raw.startsWith("🇬🇧") ? "GB" : raw.startsWith("🇫🇷") ? "FR" : "";
}

function nodeFlagSelect(id, flagCode) {
  const image = '<img class="flag-icon node-flag-preview' + (flagCode ? "" : " hidden") + '"'
    + (flagCode ? ' src="/flags/' + flagCode.toLowerCase() + '.png"' : "")
    + ' alt="' + escapeHtml(flagCode) + '">';
  return '<span class="node-flag-picker">' + image
    + '<select class="node-flag-select" data-id="' + escapeHtml(id) + '">'
    + nodeFlagChoices.map(([code, label]) => '<option value="' + code + '"' + (code === flagCode ? " selected" : "") + '>' + label + "</option>").join("")
    + "</select></span>";
}

function updateFlagPreview(select, image) {
  const code = select.value.toLowerCase();
  image.classList.toggle("hidden", !code);
  image.alt = select.value;
  if (code) image.src = "/flags/" + code + ".png";
}

function nodeRow(node, isLocal) {
  const id = isLocal ? "local" : node.id;
  const name = normalizedNodeName(node.name || (isLocal ? "本机节点" : id));
  const flagCode = node.flagCode || inferredNodeFlag(node.name);
  const host = node.publicHost || "";
  const version = isLocal ? "local" : (node.version || "等待接入");
  const status = !node.online ? "控制面离线" : (node.frpsOnline === false ? "控制面在线 · frps 异常" : "在线");
  const statusHealthy = node.online && node.frpsOnline !== false;
  const actions = '<button class="node-save" data-id="' + escapeHtml(id) + '">保存</button>'
    + (isLocal ? " <span class=\"node-local\">本机</span>"
      : ' <button class="node-restart" data-id="' + escapeHtml(id) + '">重启</button> <button class="danger node-delete" data-id="' + escapeHtml(id) + '">删除</button>');
  return '<tr><td><div class="node-editor">' + nodeFlagSelect(id, flagCode)
    + '<input class="node-name-edit" data-id="' + escapeHtml(id) + '" value="' + escapeHtml(name) + '"></div><small>' + escapeHtml(version) + "</small></td>"
    + '<td><input class="node-host-edit" data-id="' + escapeHtml(id) + '" value="' + escapeHtml(host) + '"><small>frps : ' + (node.frpsPort || 7000) + "</small></td>"
    + '<td><span class="tag ' + (statusHealthy ? "" : "off") + '">' + status + "</span></td>"
    + "<td>" + (node.activeClients || 0) + "</td><td>" + (node.activeProxies || 0) + "</td>"
    + "<td>" + (isLocal ? "刚刚" : new Date(node.lastSeen).toLocaleString()) + "</td><td>" + actions + "</td></tr>";
}

loadNodes = async function () {
  try {
    const [status, nodes] = await Promise.all([api("/api/frps/install-status"), api("/api/admin/nodes")]);
    const overview = snapshot || await api("/api/overview");
    $("#install-status").textContent = status.message || (status.installed ? "本机 frps 已安装，可由主控面板管理。" : "本机尚未安装，可点击右上角自动修复。");
    const local = {
      name: overview.localNodeName || "本机节点",
      flagCode: overview.localNodeFlagCode || "",
      publicHost: overview.publicHost || "",
      frpsPort: overview.bindPort || 7000,
      online: overview.reachable,
      frpsOnline: overview.reachable,
      activeClients: Number($("#metric-clients").textContent || 0),
      activeProxies: Number($("#metric-proxies").textContent || 0)
    };
    $("#nodes-body").innerHTML = nodeRow(local, true) + nodes.map(node => nodeRow(node, false)).join("");
    $$(".node-flag-select").forEach(select => {
      select.onchange = () => updateFlagPreview(select, select.closest(".node-flag-picker").querySelector(".node-flag-preview"));
    });
    $$(".node-save").forEach(button => button.onclick = async () => {
      try {
        const id = button.dataset.id;
        const name = $('.node-name-edit[data-id="' + id + '"]').value;
        const flagCode = $('.node-flag-select[data-id="' + id + '"]').value;
        const publicHost = $('.node-host-edit[data-id="' + id + '"]').value;
        await api("/api/admin/nodes/" + id, { method: "PUT", body: JSON.stringify({ name, flagCode, publicHost }) });
        toast("节点信息已保存");
        snapshot = null;
        loadNodes();
      } catch (error) { toast(error.message); }
    });
    $$(".node-restart").forEach(button => button.onclick = async () => {
      try {
        const result = await api("/api/admin/nodes/" + button.dataset.id + "/service/restart", { method: "POST" });
        toast(result.message);
      } catch (error) { toast(error.message); }
    });
    $$(".node-delete").forEach(button => button.onclick = async () => {
      try {
        const result = await api("/api/admin/nodes/" + button.dataset.id, { method: "DELETE" });
        toast(result.message);
        snapshot = null;
        loadNodes();
      } catch (error) { toast(error.message); }
    });
  } catch (error) { toast(error.message); }
};

const enrollmentFlagSelect = $("#node-enrollment-flag");
const enrollmentFlagImage = $("#node-enrollment-flag-image");
enrollmentFlagSelect.onchange = () => updateFlagPreview(enrollmentFlagSelect, enrollmentFlagImage);

function powerShellQuote(value) {
  return "'" + String(value || "").replaceAll("'", "''") + "'";
}

function offlineDeploymentCommand(result, host) {
  return [
    "$dir = Join-Path $env:USERPROFILE 'Downloads\\ZRfrp-node'",
    "New-Item -ItemType Directory -Force -Path $dir | Out-Null",
    "Invoke-WebRequest " + powerShellQuote(result.serverPackageUrl) + " -OutFile (Join-Path $dir " + powerShellQuote(result.serverFileName) + ")",
    "Invoke-WebRequest " + powerShellQuote(result.frpPackageUrl) + " -OutFile (Join-Path $dir " + powerShellQuote(result.frpFileName) + ")",
    "Invoke-WebRequest " + powerShellQuote(result.offlineScriptUrl) + " -OutFile (Join-Path $dir 'zrfrp-node-offline.sh')",
    "ssh " + powerShellQuote("root@" + host) + " 'mkdir -p /tmp/zrfrp-node'",
    "scp (Join-Path $dir " + powerShellQuote(result.serverFileName) + ") (Join-Path $dir " + powerShellQuote(result.frpFileName) + ") (Join-Path $dir 'zrfrp-node-offline.sh') " + powerShellQuote("root@" + host + ":/tmp/zrfrp-node/"),
    "ssh " + powerShellQuote("root@" + host) + " 'bash /tmp/zrfrp-node/zrfrp-node-offline.sh'"
  ].join("\r\n");
}

$("#node-enrollment-form").onsubmit = async event => {
  event.preventDefault();
  const status = $("#node-enrollment-status");
  const command = $("#node-enrollment-command");
  status.textContent = "正在生成节点部署命令...";
  try {
    const result = await api("/api/admin/nodes/enrollment", {
      method: "POST",
      body: JSON.stringify({
        name: $("#node-enrollment-name").value,
        publicHost: $("#node-enrollment-host").value,
        masterUrl: $("#node-enrollment-master-url").value,
        flagCode: $("#node-enrollment-flag").value,
        architecture: $("#node-enrollment-architecture").value
      })
    });
    command.value = result.command;
    command.classList.remove("hidden");
    $("#node-enrollment-actions").classList.remove("hidden");
    const offlineCommand = $("#node-offline-command");
    offlineCommand.value = offlineDeploymentCommand(result, $("#node-enrollment-host").value.trim());
    offlineCommand.dataset.scriptUrl = result.offlineScriptUrl;
    offlineCommand.classList.remove("hidden");
    $("#node-offline-actions").classList.remove("hidden");
    status.textContent = "节点 " + result.name + " 已登记，执行下方命令后将自动上线。";
    toast("节点部署命令已生成");
    snapshot = null;
    loadNodes();
  } catch (error) {
    status.textContent = error.message;
    toast(error.message);
  }
};

$("#download-node-offline-script").onclick = () => {
  const url = $("#node-offline-command").dataset.scriptUrl;
  if (!url) return;
  const link = document.createElement("a");
  link.href = url;
  link.download = "zrfrp-node-offline.sh";
  document.body.appendChild(link);
  link.click();
  link.remove();
};

$("#copy-node-offline-command").onclick = async () => {
  const command = $("#node-offline-command");
  try {
    await copyText(command.value, command);
    toast("Windows 离线部署命令已复制");
  } catch (error) { toast(error.message); }
};
