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

# Copy both services
COPY --from=publish-api /app/api/publish /app/api
COPY --from=publish-web /app/web/publish /app/web

# Install Caddy
RUN apt-get update && \
    apt-get install -y curl gpg && \
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
        printf '#!/bin/bash\nset -e\ncaddy run --config /etc/caddy/Caddyfile &\nsleep 2\ncd /app/api && dotnet HnHMapperServer.Api.dll &\ncd /app/web && dotnet HnHMapperServer.Web.dll &\nwait\n' > /app/start.sh && \
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
        printf ':80 {\n    reverse_proxy /client/* localhost:5000\n    reverse_proxy /map/api/* localhost:5000\n    reverse_proxy /map/grids/* localhost:5000\n    reverse_proxy /map/updates localhost:5000\n    reverse_proxy /* localhost:5001\n}\n' > /etc/caddy/Caddyfile && \
        echo "Default Caddyfile created"; \
    fi && \
    ls -la /etc/caddy/Caddyfile

EXPOSE 80

CMD ["/app/start.sh"]

