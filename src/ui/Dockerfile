FROM node:22-alpine

WORKDIR /app

COPY src/ui/package.json src/ui/package-lock.json* ./
RUN npm install

COPY src/ui/ .

EXPOSE 3000

CMD ["npm", "run", "dev"]
