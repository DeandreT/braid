# Weft runtime images. Build from the repository root with the classic builder or BuildKit:
#   docker build -f deploy/container/Dockerfile --target weft -t weft:local .
#   docker build -f deploy/container/Dockerfile --target edge -t weft-edge:local .
#   docker build -f deploy/container/Dockerfile --target weft-worker -t weft-worker:local .
#   docker build -f deploy/container/Dockerfile --target weft-mapping-endpoint -t weft-mapping-endpoint:local .
# See docs/runtime-packaging.md. Every base image is pinned by digest.

ARG NODE_IMAGE=docker.io/library/node:22.23.3-bookworm-slim@sha256:c3de60bf2f9dd0ac6370e6117950ff62d6e339527e7472301c9c78a017978392
ARG ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
ARG CADDY_IMAGE=docker.io/library/caddy:2.10.2-alpine@sha256:4c6e91c6ed0e2fa03efd5b44747b625fec79bc9cd06ac5235a779726618e530d
ARG DOCKER_CLI_IMAGE=docker.io/library/docker:29.8.1-cli@sha256:018edbc908e08fcc9dbf029c812c34251e9b4719e6f71ca0e5eae2a987d014ca

# Same-replica edge for the trustedProxyOneHop host transport.
FROM ${CADDY_IMAGE} AS edge
# File capabilities would make exec fail once every capability is dropped; port 8080 needs none.
RUN setcap -r /usr/bin/caddy
COPY deploy/container/Caddyfile /etc/weft/Caddyfile
ENV XDG_CONFIG_HOME=/tmp/caddy/config XDG_DATA_HOME=/tmp/caddy/data
USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["/usr/bin/caddy", "run", "--config", "/etc/weft/Caddyfile", "--adapter", "caddyfile"]

FROM ${NODE_IMAGE} AS web
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 NPM_CONFIG_UPDATE_NOTIFIER=false NPM_CONFIG_FUND=false NPM_CONFIG_AUDIT=false NG_BUILD_MAX_WORKERS=2
RUN npm install --global npm@12.1.0
WORKDIR /src
COPY package.json package-lock.json .npmrc ./
COPY apps/web/package.json apps/web/package.json
RUN npm ci
COPY apps/web apps/web
RUN npm run build

FROM ${ASPNET_IMAGE} AS build
# The pinned SDK band is published as an archive, not as an SDK image.
ARG DOTNET_SDK_VERSION=10.0.112
ARG DOTNET_SDK_SHA512=1ac949dfd3929855e3a102a5b12f843967a784ad2d911dfcfff6abdf81a2e78bccf25ddc055547de02b36133bf792aaf97fcba8db8aa6b52ac2e018b86935d05
USER root
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && curl --fail --silent --show-error --location --retry 3 --output /tmp/dotnet-sdk.tar.gz \
        "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" \
    && echo "${DOTNET_SDK_SHA512}  /tmp/dotnet-sdk.tar.gz" | sha512sum --check --status \
    && tar --extract --gzip --file /tmp/dotnet-sdk.tar.gz --directory /usr/share/dotnet \
    && rm /tmp/dotnet-sdk.tar.gz
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false DOTNET_PROCESSOR_COUNT=2
WORKDIR /src
COPY global.json ./
COPY migrations migrations
# Self-authored native examples are embedded in the API assembly.
COPY engines/ferrule-host/tests/fixtures engines/ferrule-host/tests/fixtures
COPY dotnet dotnet
COPY docs/user-guide docs/user-guide
RUN dotnet restore dotnet/apps/Weft.Server/Weft.Server.csproj --locked-mode \
    && for tool in DatabaseTool StorageTool MaintenanceTool SessionKeyTool; do \
        dotnet restore "dotnet/tools/Weft.$tool/Weft.$tool.csproj" --locked-mode || exit 1; done
COPY --from=web /src/apps/web/dist/weft/browser apps/web/dist/weft/browser
RUN dotnet publish dotnet/apps/Weft.Server/Weft.Server.csproj --configuration Release --no-restore \
        --output /out/app/server -p:UseAppHost=false -p:OpenApiGenerateDocuments=false \
    && test -f /out/app/server/wwwroot/index.html \
    && for tool in database:DatabaseTool storage:StorageTool maintenance:MaintenanceTool session-keys:SessionKeyTool; do \
        dotnet publish "dotnet/tools/Weft.${tool#*:}/Weft.${tool#*:}.csproj" --configuration Release --no-restore \
            --output "/out/app/tools/${tool%%:*}" -p:UseAppHost=false || exit 1; done \
    && install -d -m 0700 /out/state/weft/storage /out/state/weft/keys /out/state/run/secrets

# Server and one-shot operator commands share one image and one dependency closure.
FROM ${ASPNET_IMAGE} AS weft
COPY --from=build /out/app /app
COPY deploy/container/weft-entrypoint.sh /app/bin/weft-entrypoint
COPY --from=build --chown=1654:1654 /out/state/weft /var/lib/weft
COPY --from=build --chown=1654:1654 /out/state/run /run/weft
USER 1654:1654
WORKDIR /app
ENTRYPOINT ["/app/bin/weft-entrypoint"]
CMD ["server"]

FROM ${DOCKER_CLI_IMAGE} AS docker-cli

# Hosted inspection worker: Weft.Worker with the Docker CLI. It runs the Ferrule image through a
# Docker daemon for the Session server, which reaches it only over the pinned inspection transport.
# These stages come last so the classic builder does not build them for the other targets.
FROM build AS worker-build
RUN dotnet restore dotnet/apps/Weft.Worker/Weft.Worker.csproj --locked-mode \
    && dotnet publish dotnet/apps/Weft.Worker/Weft.Worker.csproj --configuration Release --no-restore \
        --output /out/worker -p:UseAppHost=false

FROM ${ASPNET_IMAGE} AS weft-worker
COPY --from=worker-build /out/worker /app/worker
COPY deploy/container/weft-entrypoint.sh /app/bin/weft-entrypoint
COPY --from=docker-cli /usr/local/bin/docker /usr/bin/docker
COPY --from=build --chown=1654:1654 /out/state/run /run/weft
USER 1654:1654
WORKDIR /app
ENTRYPOINT ["/app/bin/weft-entrypoint"]
CMD ["worker"]

# Machine-facing mapping endpoint for one registered target, with the release operator tool that qualifies
# builds and activates releases for it. No Docker CLI, curl or package manager: health is the host's own
# HTTP probe of /v1/health/live and /v1/health/ready.
FROM build AS endpoint-build
RUN dotnet restore dotnet/apps/Weft.MachineEndpoint/Weft.MachineEndpoint.csproj --locked-mode \
    && dotnet restore dotnet/tools/Weft.ReleaseTool/Weft.ReleaseTool.csproj --locked-mode \
    && dotnet publish dotnet/apps/Weft.MachineEndpoint/Weft.MachineEndpoint.csproj --configuration Release --no-restore \
        --output /out/endpoint -p:UseAppHost=false -p:OpenApiGenerateDocuments=false \
    && dotnet publish dotnet/tools/Weft.ReleaseTool/Weft.ReleaseTool.csproj --configuration Release --no-restore \
        --output /out/tools/release -p:UseAppHost=false -p:OpenApiGenerateDocuments=false

FROM ${ASPNET_IMAGE} AS weft-mapping-endpoint
COPY --from=endpoint-build /out/endpoint /app/endpoint
COPY --from=endpoint-build /out/tools /app/tools
COPY deploy/container/weft-entrypoint.sh /app/bin/weft-entrypoint
COPY --from=build --chown=1654:1654 /out/state/run /run/weft
USER 1654:1654
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["/app/bin/weft-entrypoint"]
CMD ["endpoint"]

# Local profile only: the Docker compiled runtime drives the host Docker daemon, which is equivalent to host root.
FROM weft-mapping-endpoint AS weft-mapping-endpoint-local-docker
COPY --from=docker-cli /usr/local/bin/docker /usr/bin/docker
