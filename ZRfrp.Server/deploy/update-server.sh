#!/usr/bin/env bash
set -euo pipefail

REPOSITORY="${ZRFRP_REPOSITORY:-3317603015whw-art/ZRfrp}"
VERSION="${ZRFRP_VERSION:-latest}"

case "$(uname -m)" in
  x86_64|amd64) RID="linux-x64" ;;
  aarch64|arm64) RID="linux-arm64" ;;
  *) exit 1 ;;
esac

TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

if [[ "${VERSION}" == "latest" ]]; then
  URL="https://github.com/${REPOSITORY}/releases/latest/download/zrfrp-server-${RID}.tar.gz"
else
  URL="https://github.com/${REPOSITORY}/releases/download/${VERSION}/zrfrp-server-${RID}.tar.gz"
fi

if ! curl --fail --location --retry 8 --retry-all-errors --retry-delay 2 \
  --connect-timeout 20 --speed-time 30 --speed-limit 1024 \
  "${URL}" -o "${TMP}/server.tar.gz"; then
  cat >&2 <<EOF
下载更新包失败，通常是当前服务器访问 GitHub Release CDN 超时或返回 5xx。
可以稍后在面板重试，或在 SSH 中手动执行：

curl -fsSL https://raw.githubusercontent.com/${REPOSITORY}/main/ZRfrp.Server/deploy/install.sh | sudo bash
EOF
  exit 22
fi

tar -xzf "${TMP}/server.tar.gz" -C "${TMP}"
SERVER_DIR="$(find "${TMP}" -maxdepth 2 -type f -name zrfrp-server -printf '%h\n' | head -n 1)"
if [[ -z "${SERVER_DIR}" ]]; then
  echo "更新包结构无效：未找到 zrfrp-server。" >&2
  exit 1
fi
cp -a "${SERVER_DIR}/." /opt/zrfrp/server/
chmod 0755 /opt/zrfrp/server/zrfrp-server
chown -R zrfrp:zrfrp /opt/zrfrp/server
UNIT="zrfrp-update-restart-$(date +%s)"
if ! systemd-run --collect --unit="${UNIT}" --on-active=2s /usr/bin/systemctl restart zrfrp-server zrfrp-frps; then
  nohup /bin/sh -c 'sleep 2; /usr/bin/systemctl restart zrfrp-server zrfrp-frps' >/dev/null 2>&1 &
fi
