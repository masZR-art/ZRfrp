(function () {
  "use strict";

  const colors = ["#21c494", "#65aef2", "#e2ad55", "#d95b6a", "#9b8afb", "#62d2d6", "#cbd5e1", "#7dd3a7"];
  const state = new WeakMap();

  function bytes(value) {
    let number = Math.max(0, Number(value || 0));
    for (const unit of ["B", "KB", "MB", "GB", "TB"]) {
      if (number < 1024 || unit === "TB") {
        const digits = number < 10 && unit !== "B" ? 1 : 0;
        return number.toFixed(digits) + " " + unit;
      }
      number /= 1024;
    }
    return "0 B";
  }

  function total(item) {
    return Number(item?.totalBytes || 0);
  }

  function prepareCanvas(canvas, height) {
    const width = Math.max(260, Math.round(canvas.getBoundingClientRect().width || canvas.parentElement.clientWidth));
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.round(width * ratio);
    canvas.height = Math.round(height * ratio);
    canvas.style.height = height + "px";
    const context = canvas.getContext("2d");
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    return { context, width, height };
  }

  function compactBytes(value) {
    const number = Number(value || 0);
    if (number >= 1024 ** 4) return (number / 1024 ** 4).toFixed(1) + "T";
    if (number >= 1024 ** 3) return (number / 1024 ** 3).toFixed(1) + "G";
    if (number >= 1024 ** 2) return (number / 1024 ** 2).toFixed(1) + "M";
    if (number >= 1024) return (number / 1024).toFixed(1) + "K";
    return Math.round(number) + "B";
  }

  function drawTrend(root, points) {
    const canvas = root.querySelector('[data-chart="trend"]');
    if (!canvas) return;
    const empty = canvas.parentElement.querySelector(".chart-empty");
    const values = points.map(point => Number(point.trafficInBytes || 0) + Number(point.trafficOutBytes || 0));
    const hasData = values.some(value => value > 0);
    empty.classList.toggle("hidden", hasData);
    const { context, width, height } = prepareCanvas(canvas, 250);
    const pad = { left: 54, right: 18, top: 18, bottom: 34 };
    const chartWidth = width - pad.left - pad.right;
    const chartHeight = height - pad.top - pad.bottom;
    const maximum = Math.max(1, ...points.flatMap(point => [Number(point.trafficInBytes || 0), Number(point.trafficOutBytes || 0)]));
    context.clearRect(0, 0, width, height);
    context.font = '11px "Segoe UI", sans-serif';
    context.lineWidth = 1;
    for (let index = 0; index <= 4; index++) {
      const y = pad.top + chartHeight * index / 4;
      context.strokeStyle = "rgba(142,171,196,.18)";
      context.beginPath();
      context.moveTo(pad.left, y);
      context.lineTo(width - pad.right, y);
      context.stroke();
      context.fillStyle = "#7896b0";
      context.textAlign = "right";
      context.textBaseline = "middle";
      context.fillText(compactBytes(maximum * (1 - index / 4)), pad.left - 9, y);
    }
    if (!points.length) return;
    const xFor = index => pad.left + (points.length === 1 ? chartWidth / 2 : chartWidth * index / (points.length - 1));
    const yFor = value => pad.top + chartHeight - chartHeight * Number(value || 0) / maximum;
    const drawSeries = (key, color, fill) => {
      context.beginPath();
      points.forEach((point, index) => {
        const x = xFor(index), y = yFor(point[key]);
        if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
      });
      if (fill && points.length > 1) {
        context.lineTo(xFor(points.length - 1), pad.top + chartHeight);
        context.lineTo(xFor(0), pad.top + chartHeight);
        context.closePath();
        context.fillStyle = fill;
        context.fill();
        context.beginPath();
        points.forEach((point, index) => {
          const x = xFor(index), y = yFor(point[key]);
          if (index === 0) context.moveTo(x, y); else context.lineTo(x, y);
        });
      }
      context.strokeStyle = color;
      context.lineWidth = 2;
      context.lineJoin = "round";
      context.stroke();
    };
    drawSeries("trafficInBytes", colors[0], "rgba(33,196,148,.10)");
    drawSeries("trafficOutBytes", colors[1], "rgba(101,174,242,.08)");

    const labelIndexes = [...new Set([0, Math.floor((points.length - 1) / 2), points.length - 1])];
    context.fillStyle = "#7896b0";
    context.textBaseline = "top";
    labelIndexes.forEach(index => {
      const time = new Date(points[index].time);
      const label = points.length > 40
        ? `${time.getMonth() + 1}/${time.getDate()}`
        : time.toLocaleString([], { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" });
      context.textAlign = index === 0 ? "left" : index === points.length - 1 ? "right" : "center";
      context.fillText(label, xFor(index), height - 22);
    });

    canvas._trafficHitTest = event => {
      const rect = canvas.getBoundingClientRect();
      const x = event.clientX - rect.left;
      const index = Math.max(0, Math.min(points.length - 1, Math.round((x - pad.left) / chartWidth * (points.length - 1))));
      return {
        x: xFor(index),
        y: Math.min(yFor(points[index].trafficInBytes), yFor(points[index].trafficOutBytes)),
        html: `<b>${new Date(points[index].time).toLocaleString()}</b><span>入站 ${bytes(points[index].trafficInBytes)}</span><span>出站 ${bytes(points[index].trafficOutBytes)}</span>`
      };
    };
    bindTooltip(canvas);
  }

  function drawDonut(root, key, items) {
    const canvas = root.querySelector(`[data-chart="${key}"]`);
    const legend = root.querySelector(`[data-legend="${key}"]`);
    if (!canvas || !legend) return;
    const sum = items.reduce((value, item) => value + total(item), 0);
    const { context, width, height } = prepareCanvas(canvas, 172);
    const size = Math.min(width, height) - 22;
    const centerX = width / 2, centerY = height / 2;
    context.clearRect(0, 0, width, height);
    context.lineWidth = Math.max(15, size * .17);
    context.strokeStyle = "#183044";
    context.beginPath();
    context.arc(centerX, centerY, size * .34, 0, Math.PI * 2);
    context.stroke();
    let start = -Math.PI / 2;
    const arcs = [];
    items.forEach((item, index) => {
      if (total(item) <= 0 || sum <= 0) return;
      const end = start + Math.PI * 2 * total(item) / sum;
      context.strokeStyle = colors[index % colors.length];
      context.beginPath();
      context.arc(centerX, centerY, size * .34, start, end);
      context.stroke();
      arcs.push({ start, end, item, color: colors[index % colors.length] });
      start = end;
    });
    canvas.closest(".donut-wrap").querySelector(".donut-total strong").textContent = bytes(sum);
    legend.innerHTML = items.length
      ? items.map((item, index) => `<div><i style="background:${colors[index % colors.length]}"></i><span title="${escapeAttribute(item.label)}">${escapeText(item.label)}</span><strong>${bytes(total(item))}</strong></div>`).join("")
      : '<p class="chart-no-data">当前区间尚无流量</p>';
    canvas._trafficHitTest = event => {
      const rect = canvas.getBoundingClientRect();
      const x = event.clientX - rect.left - centerX;
      const y = event.clientY - rect.top - centerY;
      const distance = Math.sqrt(x * x + y * y);
      if (distance < size * .22 || distance > size * .46 || !arcs.length) return null;
      let angle = Math.atan2(y, x);
      if (angle < -Math.PI / 2) angle += Math.PI * 2;
      const arc = arcs.find(candidate => angle >= candidate.start && angle <= candidate.end);
      return arc ? { x: event.clientX - rect.left, y: event.clientY - rect.top, html: `<b>${escapeText(arc.item.label)}</b><span>${bytes(total(arc.item))}</span>` } : null;
    };
    bindTooltip(canvas);
  }

  function renderRanking(root, key, items) {
    const list = root.querySelector(`[data-ranking="${key}"]`);
    if (!list) return;
    const maximum = Math.max(1, ...items.map(total));
    list.innerHTML = items.length ? items.map((item, index) => {
      const width = Math.max(2, total(item) / maximum * 100);
      return `<div class="ranking-row"><div><span class="ranking-index">${index + 1}</span><strong title="${escapeAttribute(item.label)}">${escapeText(item.label)}</strong><b>${bytes(total(item))}</b></div><span class="ranking-track"><i style="width:${width}%;background:${colors[index % colors.length]}"></i></span><small>入站 ${bytes(item.trafficInBytes)} · 出站 ${bytes(item.trafficOutBytes)}</small></div>`;
    }).join("") : '<p class="chart-no-data">当前区间尚无流量</p>';
  }

  function bindTooltip(canvas) {
    if (canvas.dataset.tooltipBound === "true") return;
    canvas.dataset.tooltipBound = "true";
    const tooltip = canvas.parentElement.querySelector(".chart-tooltip") || document.createElement("div");
    if (!tooltip.parentElement) {
      tooltip.className = "chart-tooltip hidden";
      canvas.parentElement.appendChild(tooltip);
    }
    canvas.addEventListener("mousemove", event => {
      const hit = canvas._trafficHitTest?.(event);
      if (!hit) {
        tooltip.classList.add("hidden");
        return;
      }
      tooltip.innerHTML = hit.html;
      tooltip.style.left = Math.max(8, Math.min(canvas.clientWidth - 158, hit.x + 12)) + "px";
      tooltip.style.top = Math.max(6, hit.y - 58) + "px";
      tooltip.classList.remove("hidden");
    });
    canvas.addEventListener("mouseleave", () => tooltip.classList.add("hidden"));
  }

  function escapeText(value) {
    return String(value ?? "").replace(/[&<>"']/g, character => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]);
  }

  function escapeAttribute(value) {
    return escapeText(value).replace(/`/g, "&#96;");
  }

  function render(root, data) {
    if (!root || !data) return;
    state.set(root, data);
    const periodTotal = Number(data.periodTrafficInBytes || 0) + Number(data.periodTrafficOutBytes || 0);
    root.querySelector('[data-stat="period-total"]').textContent = bytes(periodTotal);
    root.querySelector('[data-stat="period-in"]').textContent = bytes(data.periodTrafficInBytes);
    root.querySelector('[data-stat="period-out"]').textContent = bytes(data.periodTrafficOutBytes);
    root.querySelector('[data-stat="status"]').textContent = data.hasHistory
      ? "统计每 15 分钟持久化，页面每 10 秒更新"
      : "历史统计从本版本安装后开始记录";
    drawTrend(root, data.timeline || []);
    drawDonut(root, "nodes", data.nodes || []);
    drawDonut(root, "protocols", data.protocols || []);
    renderRanking(root, "accounts", data.accounts || []);
    renderRanking(root, "tunnels", data.tunnels || []);
  }

  let resizeTimer = 0;
  window.addEventListener("resize", () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      document.querySelectorAll("[data-traffic-dashboard]").forEach(root => {
        const data = state.get(root);
        if (data && root.offsetParent !== null) render(root, data);
      });
    }, 120);
  });

  window.ZRfrpTrafficCharts = { render };
}());
