#!/bin/sh
# Self-signs a certificate for the 443 listener when none has been mounted at /etc/nginx/tls.
#
# Enough because the only thing that connects to 443 is the NAS front proxy, which terminates the
# real certificate and re-encrypts to here without validating this one. That keeps the LAN leg
# encrypted; it does not authenticate either end. The server's Proxy:KnownNetworks is what stops
# another host spoofing forwarded headers.
#
# Mount a real pair at /etc/nginx/tls/{cert,key}.pem to skip this.

set -e

dir=/etc/nginx/tls

if [ -s "$dir/cert.pem" ] && [ -s "$dir/key.pem" ]; then
    echo "tls: using the certificate mounted at $dir"
    exit 0
fi

mkdir -p "$dir"

openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 3650 \
    -subj "/CN=${TLS_COMMON_NAME:-familiar-client}" \
    -keyout "$dir/key.pem" -out "$dir/cert.pem" >/dev/null 2>&1

echo "tls: self-signed a certificate for the 443 listener"
