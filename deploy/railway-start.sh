#!/bin/sh

# Start API in background
cd /app/api
dotnet HnHMapperServer.Api.dll --urls=http://localhost:8081 &
API_PID=$!

# Start Web in background
cd /app/web
dotnet HnHMapperServer.Web.dll --urls=http://localhost:8082 &
WEB_PID=$!

# Wait for services to start
sleep 5

# Start Caddy (main process)
exec caddy run --config /etc/caddy/Caddyfile