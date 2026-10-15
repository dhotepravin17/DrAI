#!/usr/bin/env bash
# One-time server preparation for Ubuntu 22.04/24.04 (x86-64). Run as root or with sudo from deploy/vps:
#   sudo bash scripts/setup.sh
set -euo pipefail
cd "$(dirname "$0")/.."

if [[ "$(uname -m)" != "x86_64" ]]; then
  echo "SQL Server containers need an x86-64 (AMD/Intel) server; this one is $(uname -m)." >&2
  exit 1
fi

echo "== Installing Docker (official convenience script) =="
if ! command -v docker >/dev/null; then
  curl -fsSL https://get.docker.com | sh
fi
systemctl enable --now docker

echo "== Firewall: SSH, HTTP, HTTPS only =="
if command -v ufw >/dev/null; then
  ufw allow OpenSSH
  ufw allow 80/tcp
  ufw allow 443/tcp
  ufw allow 443/udp
  ufw --force enable
fi

echo "== Swap (SQL Server needs headroom on 2-4 GB servers) =="
mem_mb=$(awk '/MemTotal/ {print int($2/1024)}' /proc/meminfo)
if [[ $mem_mb -lt 6000 && ! -f /swapfile ]]; then
  fallocate -l 2G /swapfile
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
if [[ $mem_mb -lt 1900 ]]; then
  echo "WARNING: ${mem_mb} MB RAM. SQL Server needs at least 2 GB; use a 4 GB server for production." >&2
fi

echo "== Backup folder (SQL Server runs as uid 10001) =="
mkdir -p backups
chown 10001:0 backups
chmod 770 backups

echo "== Automatic security updates =="
apt-get install -y unattended-upgrades >/dev/null
dpkg-reconfigure -f noninteractive unattended-upgrades

echo
echo "Done. Next: bash scripts/generate-secrets.sh, edit .env, then: docker compose up -d"
