# syntax=docker/dockerfile:1.4

### 1) Build stage using .NET SDK
FROM mcr.microsoft.com/dotnet/sdk:8.0.121 AS build
WORKDIR /src

# Copy solution and project files
COPY BareProx.sln ./
COPY BareProx.csproj ./

# Restore dependencies
RUN dotnet restore

# Copy the entire project (everything in the current dir)
COPY . ./

# Set working directory to project root
WORKDIR /src

# Publish self-contained for linux-x64
RUN dotnet publish BareProx.csproj -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=false -o /app/publish

### 2) Runtime stage
FROM debian:bookworm-slim AS runtime
WORKDIR /app

# Install minimal dependencies needed by self-contained .NET app
RUN apt-get update && apt-get install -y \
    libicu72 \
    libssl3 \
    tzdata \
 && rm -rf /var/lib/apt/lists/*

 # Create a non-root user and group
RUN groupadd --gid 1001 bareprox && \
    useradd --uid 1001 --gid 1001 --shell /bin/bash --create-home bareprox

# Copy published app and set ownership
COPY --from=build /app/publish ./
RUN chown -R bareprox:bareprox /app

# Switch to the bareprox user
USER bareprox

# Expose HTTP and HTTPS ports
EXPOSE 443

# Environment setup
ENV ASPNETCORE_URLS="https://+:443" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_ENVIRONMENT=Production
# Run the app
ENTRYPOINT ["./BareProx"]
