# syntax=docker/dockerfile:1.4

########################################
# 1) BUILD STAGE 
########################################
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1.1) Copy only the solution & .csproj(s) first, to leverage layer caching.
#      If you modify a .cs file later, Docker will NOT re-run 'dotnet restore',
#      because the .csproj layer hasn’t changed.
COPY BareProx.sln            ./
COPY BareProx.csproj         ./

# If you have additional projects or shared props/NuGet.config, copy them here:
# COPY SharedLib/SharedLib.csproj    SharedLib/
# COPY NuGet.config                  ./
# COPY Directory.Build.props         ./

# 1.2) Restore dependencies (this layer is cached until .csproj or NuGet.config changes)
RUN dotnet restore --use-current-runtime

# 1.3) Now that restore is done, copy the rest of the source code (all .cs, .cshtml, etc.)
COPY . .

# 1.4) Publish as a self-contained Linux-x64 app, with trimming enabled.
#      We set PublishTrimmed=true to aggressively strip out unused DLLs/runtimes.
RUN dotnet publish \
        BareProx.csproj \
        -c Release \
        -r linux-x64 \
        --self-contained true \
        -p:PublishTrimmed=true \
        -o /app/publish

########################################
# 2) RUNTIME STAGE 
########################################
FROM debian:bookworm-slim AS runtime
WORKDIR /app

# 2.1) You can run in “invariant globalization” mode to avoid pulling in tzdata, ICU, etc.
#      That means your app won’t do culture‐specific formatting unless you embed ICU data,
#      but for most APIs it’s fine. If you do need full globalization, you’d install 'tzdata', 'icu', etc.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
ENV DOTNET_EnableDiagnostics=0
ENV ASPNETCORE_URLS="https://+:443"
ENV DOTNET_ENVIRONMENT=Production

# 2.2) Copy the trimmed, self-contained binaries from the build stage
COPY --from=build /app/publish ./

# 2.3) Create a non-root user and chown the /app folder
RUN groupadd --gid 1001 bareprox \
 && useradd  --uid 1001 --gid 1001 --shell /bin/bash --create-home bareprox \
 && chown -R bareprox:bareprox /app

USER bareprox

# 2.4) Expose only the port you need (443 for HTTPS)
EXPOSE 443

# 2.5) Launch the executable
ENTRYPOINT ["./BareProx"]
