FROM node:22-alpine AS build
WORKDIR /app
COPY src/RelayForge.Web/package*.json ./
RUN npm ci
COPY src/RelayForge.Web/ ./
RUN npm run build

FROM nginx:1.27-alpine AS final
COPY src/RelayForge.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
RUN chown -R nginx:nginx /var/cache/nginx /var/run /usr/share/nginx/html && touch /var/run/nginx.pid && chown nginx:nginx /var/run/nginx.pid
USER nginx
EXPOSE 8080
CMD ["nginx", "-g", "daemon off;"]
