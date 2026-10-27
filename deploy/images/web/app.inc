# The locations every server block serves: the API through the proxy, the app's files, and the
# single-page-app fallback. Included by server.conf, whichever variant is mounted.

root /usr/share/nginx/html;

# The web container's own health, for its probe. Says nothing about the API.
location = /healthz {
    access_log off;
    default_type text/plain;
    return 200 "ok\n";
}

# The API. 'api' is the service name in compose and the Service name on OpenShift.
location /api/ {
    proxy_pass http://api:8080;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;

    # Overwritten, never appended to: a client cannot pass its own chain through. The API trusts
    # these headers (ASPNETCORE_FORWARDEDHEADERS_ENABLED) because nothing but this proxy can reach
    # it - the API publishes no port, and on OpenShift a NetworkPolicy admits only this pod.
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;

    # Above the API's ClickHouse client timeout (180 s, Program.cs), so the API's own 504 is what
    # reaches the browser, not nginx's.
    proxy_read_timeout 200s;
    proxy_send_timeout 200s;

    # Large responses (CSV exports) stream to the client instead of filling /tmp, a small tmpfs.
    proxy_max_temp_file_size 0;
}

# Uploads of operator files: up to ImportStorage:MaxFileBytes (10 GiB), enforced by the API while
# it writes. Streamed straight through - buffering a gigabyte in nginx would need a gigabyte of
# writable disk in a container that has none.
location ~ ^/api/v1/imports/[^/]+/upload$ {
    client_max_body_size 11g;
    proxy_request_buffering off;

    proxy_pass http://api:8080;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 900s;
    proxy_send_timeout 900s;
}

# Resumable uploads (ADR-018): the tus protocol. Its PATCH carries the file's bytes and is streamed
# exactly as the single upload above; its POST, HEAD and DELETE, and the call that completes an
# upload, are small. ^~ so the general /api/ location's 1 MB body limit is never the one applied.
location ^~ /api/v1/imports/uploads {
    client_max_body_size 11g;
    proxy_request_buffering off;

    proxy_pass http://api:8080;
    proxy_http_version 1.1;
    proxy_set_header Connection "";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $remote_addr;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 900s;
    proxy_send_timeout 900s;
}

# Vite's hashed assets: the name changes when the content does, so they can be cached for good.
location /assets/ {
    include /etc/nginx/sqm/security.inc;
    add_header Cache-Control "public, max-age=31536000, immutable" always;
    try_files $uri =404;
}

# Everything else is the app: a real file if there is one, otherwise index.html, and the router
# decides. index.html itself is never cached, so a deployment is picked up on the next load.
location / {
    include /etc/nginx/sqm/security.inc;
    add_header Cache-Control "no-cache" always;
    try_files $uri $uri/ /index.html;
}
