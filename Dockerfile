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

# Copy startup script from build stage (FIXED: copy from build stage where deploy/ exists)
COPY --from=build /src/deploy/railway-start.sh /app/start.sh
RUN chmod +x /app/start.sh

# Copy Caddyfile from build stage (FIXED: copy from build stage where deploy/ exists)
COPY --from=build /src/deploy/Caddyfile.railway /etc/caddy/Caddyfile

EXPOSE 80

CMD ["/app/start.sh"]

