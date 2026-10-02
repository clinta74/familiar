# Familiar web client: the Vite build served by nginx, which also proxies /api and /ws to the server.

FROM node:24-alpine AS build
WORKDIR /src

COPY package.json package-lock.json ./
RUN npm ci

# All three tsconfigs: the root one only references the other two, and `tsc -b` fails with TS5083
# (which names nothing useful) when they're missing.
COPY tsconfig*.json vite.config.ts index.html ./
COPY src ./src
COPY public ./public
RUN npm run build

FROM nginx:1.31-alpine

# For the certificate the entrypoint self-signs when none is mounted.
RUN apk add --no-cache openssl

WORKDIR /usr/share/nginx/html
COPY --from=build /src/dist .

# The image's entrypoint runs envsubst over /etc/nginx/templates; pointing the output at /etc/nginx
# turns nginx.conf.template into nginx.conf.
ENV NGINX_ENVSUBST_OUTPUT_DIR=/etc/nginx

# Where /api and /ws go. Override in compose if the server service isn't called `server`.
ENV BACKEND_ORIGIN=http://server:8080

# Docker's embedded DNS, so the backend is resolved per request rather than once at startup.
ENV DNS_RESOLVER=127.0.0.11

COPY nginx.conf.template /etc/nginx/templates/nginx.conf.template

# Executable bit set here: the entrypoint silently skips a script it can't execute, and nginx then
# fails to find the certificate.
COPY --chmod=755 docker-entrypoint.d/05-selfsign.sh /docker-entrypoint.d/05-selfsign.sh

# Non-root. /etc/nginx must be writable by this user because the template is rendered as it,
# before nginx starts - without that the render fails quietly and nginx serves the baked-in config.
RUN addgroup -g 1000 nginx-app \
    && adduser -D -u 1000 -G nginx-app nginx-app \
    && mkdir -p /etc/nginx/tls \
    && chown -R nginx-app:nginx-app /usr/share/nginx/html /var/cache/nginx /var/log/nginx /etc/nginx

USER nginx-app

# 80 for the health check; 443 for the hop from the NAS front proxy, so X-Forwarded-Proto is
# truthfully https and the session cookie gets its Secure flag.
EXPOSE 80 443

HEALTHCHECK --interval=30s --timeout=10s --retries=3 --start-period=10s \
    CMD wget -q -O - http://localhost/health || exit 1

CMD ["nginx", "-g", "daemon off;"]
