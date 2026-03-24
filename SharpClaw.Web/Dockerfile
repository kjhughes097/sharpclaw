FROM node:22-slim AS build
WORKDIR /web

# Install dependencies first for better layer caching.
COPY package.json package-lock.json* ./
RUN npm ci

# Build the app.
COPY . .
RUN npm run build

# ── Runtime image ─────────────────────────────────────────────────────────────
FROM nginx:alpine AS runtime

COPY --from=build /web/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
