
# syntax=docker/dockerfile:1.4

### 1) Build stage - .NET 10 SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy project files
COPY BareProx.sln ./
COPY BareProx.csproj ./

# Restore dependencies
RUN dotnet restore BareProx.csproj \
    -r linux-x64 \
    -p:PublishReadyToRun=true

# Copy source
COPY . ./

# Publish self-contained with ReadyToRun
RUN dotnet publish BareProx.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -p:PublishTrimmed=false \
    -p:PublishReadyToRun=true \
    -o /app/publish

### 2) Runtime stage
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime

WORKDIR /app

# Apply Debian security updates and install required utilities
RUN apt-get update \
    && apt-get upgrade -y --no-install-recommends \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        tzdata \
        curl \
    && update-ca-certificates \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN groupadd --gid 1001 bareprox \
    && useradd --uid 1001 --gid 1001 \
       --shell /bin/bash \
       --create-home bareprox

# Copy published application
COPY --from=build --chown=1001:1001 /app/publish ./

# Run as non-root
USER bareprox

EXPOSE 443

ENV ASPNETCORE_URLS="https://+:443" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["./BareProx"]
