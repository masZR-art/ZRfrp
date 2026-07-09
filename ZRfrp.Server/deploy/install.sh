#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="${ZRFRP_REPOSITORY:-3317603015whw-art/ZRfrp}"
VERSION="${ZRFRP_VERSION:-latest}"
FRP_VERSION="${FRP_VERSION:-0.69.1}"

if [[ "${EUID}" -ne 0 ]]; then
  echo "请使用 root 运行此安装脚本。" >&2
  exit 1
fi

case "$(uname -m)" in
  x86_64|amd64) RID="linux-x64"; FRP_ARCH="amd64" ;;
  aarch64|arm64) RID="linux-arm64"; FRP_ARCH="arm64" ;;
  *) echo "暂不支持的架构: $(uname -m)" >&2; exit 1 ;;
esac

command -v curl >/dev/null || { echo "需要先安装 curl。" >&2; exit 1; }
command -v tar >/dev/null || { echo "需要先安装 tar。" >&2; exit 1; }

if [[ "${VERSION}" == "latest" ]]; then
  RELEASE_URL="https://github.com/${REPOSITORY}/releases/latest/download/zrfrp-server-${RID}.tar.gz"
else
  RELEASE_URL="https://github.com/${REPOSITORY}/releases/download/${VERSION}/zrfrp-server-${RID}.tar.gz"
fi
FRP_URL="${ZRFRP_FRP_URL:-https://github.com/fatedier/frp/releases/download/v${FRP_VERSION}/frp_${FRP_VERSION}_linux_${FRP_ARCH}.tar.gz}"

id -u zrfrp >/dev/null 2>&1 || useradd --system --home /var/lib/zrfrp --shell /usr/sbin/nologin zrfrp
install -d -o zrfrp -g zrfrp /opt/zrfrp/server /etc/zrfrp /var/lib/zrfrp /var/lib/zrfrp/keys /var/log/zrfrp

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
curl --fail --location --retry 8 --retry-all-errors --retry-delay 2 \
  --connect-timeout 20 --speed-time 30 --speed-limit 1024 \
  "${RELEASE_URL}" -o "${TMP}/server.tar.gz"
tar -xzf "${TMP}/server.tar.gz" -C "${TMP}"
SERVER_DIR="$(find "${TMP}" -maxdepth 2 -type f -name zrfrp-server -printf '%h\n' | head -n 1)"
if [[ -z "${SERVER_DIR}" ]]; then
  echo "更新包结构无效：未找到 zrfrp-server。" >&2
  exit 1
fi
systemctl stop zrfrp-server 2>/dev/null || true
cp -a "${SERVER_DIR}/." /opt/zrfrp/server/
chmod 0755 /opt/zrfrp/server/zrfrp-server

if [[ "${ZRFRP_REINSTALL_FRPS:-0}" == "1" || ! -x /opt/zrfrp/frps ]]; then
  if ! curl --fail --location --retry 8 --retry-all-errors --retry-delay 2 \
    --connect-timeout 20 --speed-time 30 --speed-limit 1024 \
    "${FRP_URL}" -o "${TMP}/frp.tar.gz"; then
    cat >&2 <<EOF
frps 下载失败，通常是当前服务器访问 GitHub Release CDN 超时或返回 5xx。
如果本机已经安装过 frps，可以稍后重试 ZRfrp Server 更新；如果是首次安装，请稍后重试或通过 ZRFRP_FRP_URL 指定可访问的 frp 压缩包地址。
EOF
    exit 22
  fi
  tar -xzf "${TMP}/frp.tar.gz" -C "${TMP}"
  install -m 0755 "${TMP}/frp_${FRP_VERSION}_linux_${FRP_ARCH}/frps" /opt/zrfrp/frps
else
  echo "检测到已有 /opt/zrfrp/frps，跳过 frp 本体下载。若需重装 frps，请设置 ZRFRP_REINSTALL_FRPS=1。"
fi

if [[ -f /etc/zrfrp/zrfrp.env ]]; then
  # shellcheck disable=SC1091
  source /etc/zrfrp/zrfrp.env
fi
ADMIN_PASSWORD="${ZRFRP_ADMIN_PASSWORD:-$(openssl rand -base64 18 | tr -d '/+=')}"
CLIENT_KEY="${ZRFRP_CLIENT_API_KEY:-$(openssl rand -hex 32)}"
FRP_TOKEN="${ZRFRP_FRP_TOKEN:-${ZRfrp__FrpAuthToken:-$(openssl rand -hex 24)}}"
PEER_KEY="${ZRFRP_PEER_KEY:-${ZRfrp__PeerKey:-$(openssl rand -hex 32)}}"
DASHBOARD_PASSWORD="$(openssl rand -hex 18)"

if [[ ! -f /etc/zrfrp/frps.toml ]]; then
  cp /opt/zrfrp/server/deploy/frps.toml.example /etc/zrfrp/frps.toml
  sed -i "0,/CHANGE_ME/s//${FRP_TOKEN}/" /etc/zrfrp/frps.toml
  sed -i "0,/CHANGE_ME/s//${DASHBOARD_PASSWORD}/" /etc/zrfrp/frps.toml
fi

cat >/etc/zrfrp/zrfrp.env <<EOF
ZRFRP_ADMIN_PASSWORD=${ADMIN_PASSWORD}
ZRFRP_CLIENT_API_KEY=${CLIENT_KEY}
ZRfrp__FrpAuthToken=${FRP_TOKEN}
ZRfrp__Mode=${ZRFRP_MODE:-master}
ZRfrp__NodeId=${ZRFRP_NODE_ID:-$(hostname)}
ZRfrp__NodeName=${ZRFRP_NODE_NAME:-本机节点}
ZRfrp__MasterUrl=${ZRFRP_MASTER_URL:-}
ZRfrp__MasterKey=${ZRFRP_MASTER_KEY:-}
ZRfrp__PeerKey=${PEER_KEY}
EOF
chmod 0600 /etc/zrfrp/zrfrp.env

SYSTEMCTL_PATH="$(command -v systemctl)"
SUDO_PATH="$(command -v sudo || true)"
if [[ -z "${SUDO_PATH}" ]]; then
  echo "需要 sudo 来授权面板执行受限的 frps 服务操作。" >&2
  exit 1
fi
cat >/etc/sudoers.d/zrfrp <<EOF
zrfrp ALL=(root) NOPASSWD: ${SYSTEMCTL_PATH} start zrfrp-frps, ${SYSTEMCTL_PATH} stop zrfrp-frps, ${SYSTEMCTL_PATH} restart zrfrp-frps, /usr/local/sbin/zrfrp-install-frps, /usr/local/sbin/zrfrp-repair-frps, /usr/local/sbin/zrfrp-update-server
EOF
chmod 0440 /etc/sudoers.d/zrfrp

PUBLIC_HOST="${ZRFRP_PUBLIC_HOST:-}"
if [[ -z "${PUBLIC_HOST}" && -f /opt/zrfrp/server/appsettings.Production.json ]]; then
  PUBLIC_HOST="$(sed -n 's/.*"PublicHost"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    /opt/zrfrp/server/appsettings.Production.json | head -n 1)"
fi
if [[ -z "${PUBLIC_HOST}" ]]; then
  PUBLIC_HOST="$(curl -4 --fail --silent --max-time 4 https://api.ipify.org \
    || hostname -I | awk '{print $1}')"
fi

if [[ ! -f /opt/zrfrp/server/appsettings.Production.json ]]; then
  cat >/opt/zrfrp/server/appsettings.Production.json <<EOF
{
  "ZRfrp": {
    "PublicHost": "${PUBLIC_HOST}",
    "FrpsDashboardPassword": "${DASHBOARD_PASSWORD}"
  }
}
EOF
fi

cp /opt/zrfrp/server/deploy/zrfrp-server.service /etc/systemd/system/
cp /opt/zrfrp/server/deploy/zrfrp-frps.service /etc/systemd/system/
install -m 0755 /opt/zrfrp/server/deploy/install-frps.sh /usr/local/sbin/zrfrp-install-frps
install -m 0755 /opt/zrfrp/server/deploy/repair-frps.sh /usr/local/sbin/zrfrp-repair-frps
install -m 0755 /opt/zrfrp/server/deploy/update-server.sh /usr/local/sbin/zrfrp-update-server
chown -R zrfrp:zrfrp /opt/zrfrp /var/lib/zrfrp /var/log/zrfrp
chown root:zrfrp /etc/zrfrp
chmod 0770 /etc/zrfrp
chown root:zrfrp /etc/zrfrp/frps.toml
chmod 0640 /etc/zrfrp/frps.toml
chmod 0640 /opt/zrfrp/server/appsettings.Production.json

if [[ -n "${ZRFRP_RESET_ADMIN_PASSWORD:-}" ]]; then
  (
    cd /opt/zrfrp/server
    sudo -u zrfrp ./zrfrp-server --reset-admin "${ZRFRP_RESET_ADMIN_PASSWORD}"
  )
fi

systemctl daemon-reload
systemctl enable zrfrp-server zrfrp-frps
systemctl restart zrfrp-server zrfrp-frps

echo
echo "ZRfrp Server 已安装。"
echo "面板地址: http://${PUBLIC_HOST}:7600"
echo "初始管理员密码（若未在面板修改）: ${ADMIN_PASSWORD}"
echo "客户端 API Key: ${CLIENT_KEY}"
echo "frp Token: ${FRP_TOKEN}"
echo "节点 Peer Key: ${PEER_KEY}"
echo "请立即保存以上凭据，并通过防火墙或 HTTPS 反向代理保护 7600 端口。"
