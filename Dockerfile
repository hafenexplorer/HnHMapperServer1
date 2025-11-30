# Build stage
# Use the desired .NET SDK version for building
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build 
WORKDIR /app

# Copy solution and project files
COPY HnHMapperServer.sln ./
COPY src/HnHMapperServer.Core/HnHMapperServer.Core.csproj ./src/HnHMapperServer.Core/
COPY src/HnHMapperServer.Infrastructure/HnHMapperServer.Infrastructure.csproj ./src/HnHMapperServer.Infrastructure/
COPY src/HnHMapperServer.Services/HnHMapperServer.Services.csproj ./src/HnHMapperServer.Services/
COPY src/HnHMapperServer.Api/HnHMapperServer.Api.csproj ./src/HnHMapperServer.Api/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY . .

# Build and publish
RUN dotnet build -c Release --no-restore
RUN dotnet publish src/HnHMapperServer.Api/HnHMapperServer.Api.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Copy published application
COPY --from=build /app/publish .

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV GridStorage=/map

# Run the application
ENTRYPOINT ["dotnet", "HnHMapperServer.Api.dll"]
