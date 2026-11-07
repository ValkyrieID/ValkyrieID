#!/bin/sh
# Creates a throwaway CA and the TLS certificates for the conformance setup (conformance/docker-compose.yml). Runs in
# a one-off container; the CA key is deleted afterwards, so nothing else can be signed with it.
set -eu
cd /certs
if [ -f ca.crt ] && [ -f valkyrieid.crt ] && [ -f nginx.crt ]; then
  echo 'Certificates exist.'
  exit 0
fi

apk add --no-cache openssl >/dev/null
rm -f ./*.crt ./*.key
openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 30 -keyout ca.key -out ca.crt \
  -subj '/CN=ValkyrieID conformance test CA' \
  -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign,cRLSign' 2>/dev/null

leaf() {
  openssl req -newkey rsa:2048 -nodes -sha256 -keyout "$1.key" -out "$1.csr" -subj "/CN=$2" 2>/dev/null
  printf 'subjectAltName=DNS:%s\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' "$2" > "$1.ext"
  openssl x509 -req -sha256 -days 30 -in "$1.csr" -CA ca.crt -CAkey ca.key -CAcreateserial -out "$1.crt" -extfile "$1.ext" 2>/dev/null
}

leaf valkyrieid valkyrieid.localtest.me
leaf nginx localhost.emobix.co.uk
rm -f ca.key ca.srl ./*.csr ./*.ext
# Throwaway test keys; ValkyrieID runs as a non-root user and must read its key.
chmod 644 ./*
echo 'Created a throwaway CA and certificates for valkyrieid.localtest.me and localhost.emobix.co.uk.'
