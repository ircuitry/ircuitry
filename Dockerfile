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
# bubblewrap sandboxes each code node (no network, read-only fs) - the server falls back to resource-caps-only
# and warns at startup if it is missing. Uncomment nodejs/python3 to actually run (sandboxed) code nodes here;
# left out by default to keep the image lean. NB: bwrap inside a container needs the namespace ops allowed -
# run with --security-opt seccomp=unconfined (or apparmor=unconfined) if your host profile blocks them.
RUN apt-get update && apt-get install -y --no-install-recommends bubblewrap \
    # nodejs python3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
RUN chmod +x ./Ircuitry

# the workspace, tokens, ACLs AND the encrypted key store (secrets.json + .localkey) all live under
# IRCUITRY_HOME, so the /data volume persists everything across restarts/upgrades/recreation
ENV IRCUITRY_HOME=/data
ENV IRCUITRY_PORT=48700
ENV IRCUITRY_FLAGS=--web
VOLUME /data
EXPOSE 48700

# first start prints an admin token to the logs (docker logs <container>)
ENTRYPOINT ["/bin/sh", "-c", "exec ./Ircuitry --server --bind 0.0.0.0 --port ${IRCUITRY_PORT} ${IRCUITRY_FLAGS}"]
