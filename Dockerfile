# syntax=docker/dockerfile:1.4

### 1) Build
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src

COPY *.sln ./
COPY YourApp/*.csproj ./BareProx/
RUN dotnet restore

COPY BareProx/. ./BareProx/
WORKDIR /src/BareProx
RUN dotnet publish -c Release -o /app/publish

### 2) Runtime
FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 80
ENV ASPNETCORE_URLS=http://+:80 \
    DOTNET_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "BareProx.dll"]