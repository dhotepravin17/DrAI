#!/usr/bin/env bash
# Creates .env from .env.example (if missing) and fills empty secrets: SQL sa password, JWT signing key and
# Web Push (VAPID) keys. Existing values are never overwritten. Run from deploy/vps: bash scripts/generate-secrets.sh
set -euo pipefail
cd "$(dirname "$0")/.."

[[ -f .env ]] || cp .env.example .env
chmod 600 .env

b64url() { base64 | tr '+/' '-_' | tr -d '=\n'; }

set_if_empty() {
  local key="$1" value="$2"
  if grep -qE "^${key}=$" .env; then
    sed -i "s|^${key}=$|${key}=${value}|" .env
    echo "set ${key}"
  elif ! grep -qE "^${key}=" .env; then
    echo "${key}=${value}" >> .env
    echo "added ${key}"
  else
    echo "kept ${key}"
  fi
}

# SQL Server needs upper, lower, digit and symbol; avoid ; and = which break connection strings.
set_if_empty SA_PASSWORD "$(openssl rand -base64 24 | tr -d '/+=\n')Aa9-"
set_if_empty Jwt__SigningKey "$(openssl rand -base64 48 | tr -d '\n')"

# VAPID: P-256 key pair. Public key = uncompressed point (65 bytes), private key = 32-byte scalar, both base64url.
tmp="$(mktemp)"
openssl ecparam -name prime256v1 -genkey -noout -out "$tmp" 2>/dev/null
set_if_empty Push__PublicKey "$(openssl ec -in "$tmp" -pubout -outform DER 2>/dev/null | tail -c 65 | b64url)"
set_if_empty Push__PrivateKey "$(openssl ec -in "$tmp" -outform DER 2>/dev/null | tail -c +8 | head -c 32 | b64url)"
rm -f "$tmp"

echo
echo "Now edit .env: DOMAIN, ACME_EMAIL, Push__Subject, Bootstrap__SuperAdmins__0__MobileNumber (and SMS when ready)."
