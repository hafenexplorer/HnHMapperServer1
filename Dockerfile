# Combined Dockerfile for Railway
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Build API
WORKDIR /src
COPY ["src/HnHMapperServer.Api/HnHMapperServer.Api.csproj", "src/HnHMapperServer.Api/"]
RUN dotnet restore "src/HnHMapperServer.Api/HnHMapperServer.Api.csproj"

# Build Web
COPY ["src/HnHMapperServer.Web/HnHMapperServer.Web.csproj", "src/HnHMapperServer.Web/"]
RUN dotnet restore "src/HnHMapperServer.Web/HnHMapperServer.Web.csproj"

COPY . .
WORKDIR "/src/src/HnHMapperServer.Api"
RUN dotnet build "HnHMapperServer.Api.csproj" -c Release -o /app/api

WORKDIR "/src/src/HnHMapperServer.Web"
RUN dotnet build "HnHMapperServer.Web.csproj" -c Release -o /app/web

# Publish both
FROM build AS publish-api
WORKDIR "/src/src/HnHMapperServer.Api"
RUN dotnet publish "HnHMapperServer.Api.csproj" -c Release -o /app/api/publish

FROM build AS publish-web
WORKDIR "/src/src/HnHMapperServer.Web"
RUN dotnet publish "HnHMapperServer.Web.csproj" -c Release -o /app/web/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copy both services
COPY --from=publish-api /app/api/publish /app/api
COPY --from=publish-web /app/web/publish /app/web

# Install Caddy (FIXED: install gpg first)
RUN apt-get update && \
    apt-get install -y curl gpg && \
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg && \
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list && \
    apt-get update && \
    apt-get install -y caddy && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Copy startup script
COPY deploy/railway-start.sh /app/start.sh
RUN chmod +x /app/start.sh

# Copy Caddyfile
COPY deploy/Caddyfile.railway /etc/caddy/Caddyfile

EXPOSE 80

CMD ["/app/start.sh"]
