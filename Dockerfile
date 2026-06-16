# Mojave Authority Bot — container image
# Copyright (c) 2026 bs9zw69ff9-source. Proprietary — see LICENSE.
FROM node:20-alpine

# Run as the built-in unprivileged user
WORKDIR /app

# Install production deps first for better layer caching
COPY package.json ./
RUN npm install --omit=dev --no-audit --no-fund

# App source
COPY . .

ENV NODE_ENV=production

# The bot persists runtime state as JSON files in its working directory and
# (optionally) reads/writes faction + donator files on the game-server host.
# Mount these as volumes so data survives container restarts, e.g.:
#   docker run --env-file .env \
#     -v "$PWD/data:/app" \
#     -v "/home/steam/pavlovserver/.../ModSave:/modsave" \
#     mojave-authority-bot
VOLUME ["/app"]

USER node

CMD ["node", "index.js"]
