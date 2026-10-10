# History Speaks VR proxy — Cloud Run container.
# (gcloud run deploy --source server/ uses this; buildpacks would also work.)
FROM node:20-slim

WORKDIR /app

COPY package*.json ./
RUN npm ci --omit=dev

COPY src ./src

ENV NODE_ENV=production
EXPOSE 8080

CMD ["node", "src/index.js"]
