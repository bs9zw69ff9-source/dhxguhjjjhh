/* PM2 process config — `pm2 start ecosystem.config.js` (then `pm2 save`).
 * Copyright (c) 2026 bs9zw69ff9-source. Proprietary — see LICENSE. */
module.exports = {
  apps: [
    {
      name: "mojave-authority-bot",
      script: "index.js",
      cwd: __dirname,           // .env + JSON data files live here
      autorestart: true,
      max_restarts: 10,
      restart_delay: 5000,
      env: { NODE_ENV: "production" },
    },
  ],
};
