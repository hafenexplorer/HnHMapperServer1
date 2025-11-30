# Build stage
# Use the desired .NET SDK version for building
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build 
WORKDIR /app

# Copy solution 
COPY HnHMapperServer.sln ./

# Copy all project files - this ensures they're available for restore
# Copy src projects
COPY src/HnHMapperServer.Core/HnHMapperServer.Core.csproj src/HnHMapperServer.Core/
COPY src/HnHMapperServer.Infrastructure/HnHMapperServer.Infrastructure.csproj src/HnHMapperServer.Infrastructure/
COPY src/HnHMapperServer.Services/HnHMapperServer.Services.csproj src/HnHMapperServer.Services/
COPY src/HnHMapperServer.Api/HnHMapperServer.Api.csproj src/HnHMapperServer.Api/
COPY src/HnHMapperServer.Web/HnHMapperServer.Web.csproj src/HnHMapperServer.Web/
COPY src/HnHMapperServer.AppHost/HnHMapperServer.AppHost.csproj src/HnHMapperServer.AppHost/
COPY src/HnHMapperServer.ServiceDefaults/HnHMapperServer.ServiceDefaults.csproj src/HnHMapperServer.ServiceDefaults/

# Restore dependencies (this will work now because all project files exist)
RUN dotnet restore 

# Copy the rest of the source code
COPY src/ ./src/
COPY . ./

# Build and publish
RUN dotnet build HnHMapperServer.sln -c Release --no-restore
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
