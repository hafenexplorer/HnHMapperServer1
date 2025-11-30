# Combined Dockerfile for Railway
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Build API
WORKDIR /src
COPY ["src/HnHMapperServer.Api/HnHMapperServer.Api.csproj", "src/HnHMapperServer.Api/"]
RUN dotnet restore "src/HnHMapperServer.Api/HnHMapperServer.Api.csproj"

# Build Web
COPY ["src/HnHMapperServer.Web/HnHMapperServer.Web.csproj", "src/HnHMapperServer.Web/"]
RUN dotnet restore "src/HnHMapperServer.Web/HnHMapperServer.Web.csproj"

# Copy everything else
COPY . .

# Build API
WORKDIR "/src/src/HnHMapperServer.Api"
RUN dotnet build "HnHMapperServer.Api.csproj" -c Release -o /app/api

# Build Web
WORKDIR "/src/src/HnHMapperServer.Web"
RUN dotnet build "HnHMapperServer.Web.csproj" -c Release -o /app/web

# Publish API
FROM build AS publish-api
WORKDIR "/src/src/HnHMapperServer.Api"
RUN dotnet publish "HnHMapperServer.Api.csproj" -c Release -o /app/api/publish

# Publish Web
FROM build AS publish-web
WORKDIR "/src/src/HnHMapperServer.Web"
RUN dotnet publish "HnHMapperServer.Web.csproj" -c Release -o /app/web/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Set environment variables
# Note: ASPNETCORE_URLS is set per-service in the startup script
ENV GridStorage=/map

# Copy both services
COPY --from=publish-api /app/api/publish /app/api
COPY --from=publish-web /app/web/publish /app/web

# Install Caddy and netcat (for health checks)
RUN apt-get update && \
    apt-get install -y curl gpg netcat-openbsd && \
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg && \
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list && \
    apt-get update && \
    apt-get install -y caddy && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copy deploy directory from build stage
COPY --from=build /src/deploy /tmp/deploy

# Create startup script from copied file, ensuring proper Unix line endings and shebang
RUN if [ -f /tmp/deploy/railway-start.sh ]; then \
        echo "Found railway-start.sh, processing..." && \
        # Remove Windows line endings and ensure Unix format
        tr -d '\r' < /tmp/deploy/railway-start.sh > /app/start.sh && \
        # Ensure it starts with shebang
        if ! head -1 /app/start.sh | grep -q '^#!'; then \
            echo "Adding shebang..." && \
            sed -i '1i#!/bin/bash' /app/start.sh; \
        fi && \
        chmod +x /app/start.sh && \
        echo "Startup script created from repository file"; \
    else \
        echo "railway-start.sh not found, creating inline script..." && \
        printf '#!/bin/bash\nset -e\n\nexport GridStorage=/map\n\n# Function to wait for a service to be ready\nwait_for_service() {\n    local host=$1\n    local port=$2\n    local service=$3\n    local max_attempts=30\n    local attempt=0\n    \n    echo "Waiting for $service to be ready on $host:$port..."\n    while [ $attempt -lt $max_attempts ]; do\n        if nc -z $host $port 2>/dev/null; then\n            echo "$service is ready on $host:$port"\n            return 0\n        fi\n        attempt=$((attempt + 1))\n        sleep 1\n    done\n    echo "WARNING: $service did not become ready on $host:$port after $max_attempts attempts"\n    return 1\n}\n\n# Start API service\necho "Starting API service on port 8080..."\ncd /app/api\nexport ASPNETCORE_URLS=http://+:8080\ndotnet HnHMapperServer.Api.dll > /tmp/api.log 2>&1 &\nAPI_PID=$!\necho "API service started with PID $API_PID"\n\n# Start Web service\necho "Starting Web service on port 5001..."\ncd /app/web\nexport ASPNETCORE_URLS=http://+:5001\ndotnet HnHMapperServer.Web.dll > /tmp/web.log 2>&1 &\nWEB_PID=$!\necho "Web service started with PID $WEB_PID"\n\n# Wait for services to be ready\nif wait_for_service localhost 8080 "API"; then\n    echo "API service is ready"\nelse\n    echo "ERROR: API service failed to start. Logs:"\n    cat /tmp/api.log || true\nfi\n\nif wait_for_service localhost 5001 "Web"; then\n    echo "Web service is ready"\nelse\n    echo "ERROR: Web service failed to start. Logs:"\n    cat /tmp/web.log || true\nfi\n\n# Start Caddy after services are ready\nsleep 2\necho "Starting Caddy reverse proxy on port ${PORT:-80}..."\ncaddy run --config /etc/caddy/Caddyfile > /tmp/caddy.log 2>&1 &\nCADDY_PID=$!\necho "Caddy started with PID $CADDY_PID"\n\n# Wait for all processes (with error handling)\ntrap "echo \"Shutting down...\"; kill $API_PID $WEB_PID $CADDY_PID 2>/dev/null; exit" SIGTERM SIGINT\n\n# Wait for processes and check their status\nwait $API_PID $WEB_PID $CADDY_PID 2>/dev/null || {\n    echo "One or more processes exited. Checking logs..."\n    echo "=== API Logs ==="\n    cat /tmp/api.log || true\n    echo "=== Web Logs ==="\n    cat /tmp/web.log || true\n    echo "=== Caddy Logs ==="\n    cat /tmp/caddy.log || true\n    exit 1\n}\n' > /app/start.sh && \
        chmod +x /app/start.sh && \
        echo "Inline startup script created"; \
    fi && \
    echo "Verifying startup script:" && \
    ls -la /app/start.sh && \
    head -1 /app/start.sh && \
    [ -f /app/start.sh ] && [ -x /app/start.sh ] && \
    /bin/bash -n /app/start.sh && \
    echo "Startup script verified successfully"

# Copy Caddyfile from build stage or create default
RUN if [ -f /tmp/deploy/Caddyfile.railway ]; then \
        cp /tmp/deploy/Caddyfile.railway /etc/caddy/Caddyfile && \
        echo "Caddyfile copied from repository"; \
    else \
        printf '{$PORT:80} {\n    # API routes\n    reverse_proxy /client/* localhost:8080\n    reverse_proxy /map/api/* localhost:8080\n    reverse_proxy /map/grids/* localhost:8080\n    reverse_proxy /map/updates localhost:8080\n    # Web routes\n    reverse_proxy /* localhost:5001\n}\n' > /etc/caddy/Caddyfile && \
        echo "Default Caddyfile created"; \
    fi && \
    ls -la /etc/caddy/Caddyfile

# Expose ports
EXPOSE 80
EXPOSE 8080
EXPOSE 5001

CMD ["/app/start.sh"]


