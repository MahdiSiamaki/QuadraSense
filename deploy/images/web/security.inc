# Security headers for what nginx itself serves (the app shell and its assets). The API sets its
# own on /api/ responses (SecurityHeadersMiddleware), so this file is included only where nginx
# answers - an add_header in a location replaces, never adds to, the ones from the level above.

add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Referrer-Policy "no-referrer" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=()" always;
add_header Strict-Transport-Security $sqm_hsts always;

# Everything from this origin and nothing from anywhere else (FR-NSM-204/00 1-3: no script, style or
# font may load from outside the data centre). 'unsafe-inline' for styles only: ECharts writes its
# tooltips as HTML with style attributes, which a stricter policy blocks. Scripts stay strict.
add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'" always;
