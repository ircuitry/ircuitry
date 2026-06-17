# ircuitry headless control server in a container.
# Build:  docker build -t ircuitry-server .
# Run:    docker run -p 48700:48700 -v ircuitry-data:/data ircuitry-server
# The control port serves the WebSocket API (desktop + cockpit) and, with --web, the cockpit PWA.

# ---- build (self-contained linux-x64) ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Ircuitry/Ircuitry.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=false -o /app

# ---- run (just the deps a self-contained .NET app needs) ----
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0
WORKDIR /app
COPY --from=build /app .
RUN chmod +x ./Ircuitry

# the workspace + tokens live in a volume so they survive restarts/upgrades
ENV IRCUITRY_HOME=/data
ENV IRCUITRY_PORT=48700
ENV IRCUITRY_FLAGS=--web
VOLUME /data
EXPOSE 48700

# first start prints an admin token to the logs (docker logs <container>)
ENTRYPOINT ["/bin/sh", "-c", "exec ./Ircuitry --server --bind 0.0.0.0 --port ${IRCUITRY_PORT} ${IRCUITRY_FLAGS}"]
