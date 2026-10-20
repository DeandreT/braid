# Build from the repository root, after building the pinned native worker from this source.
# WEFT_NATIVE_IMAGE must be the immutable sha256 image ID, never a mutable registry tag.
ARG WEFT_NATIVE_IMAGE
FROM ${WEFT_NATIVE_IMAGE} AS native

FROM ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b AS sdk
ARG DOTNET_SDK_VERSION=10.0.112
ARG DOTNET_SDK_SHA512=1ac949dfd3929855e3a102a5b12f843967a784ad2d911dfcfff6abdf81a2e78bccf25ddc055547de02b36133bf792aaf97fcba8db8aa6b52ac2e018b86935d05
RUN apt-get update && apt-get install --yes --no-install-recommends ca-certificates curl libstdc++6 zlib1g libssl3t64 \
    && mkdir -p /usr/share/dotnet \
    && curl --fail --silent --show-error --location --output /tmp/sdk.tar.gz \
       "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" \
    && echo "${DOTNET_SDK_SHA512}  /tmp/sdk.tar.gz" | sha512sum --check --status \
    && tar -xzf /tmp/sdk.tar.gz -C /usr/share/dotnet && rm /tmp/sdk.tar.gz \
    && rm -rf /var/lib/apt/lists/*
ENV DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
WORKDIR /src
COPY global.json ./
COPY dotnet dotnet
RUN /usr/share/dotnet/dotnet restore dotnet/apps/Weft.BuildWorker/Weft.BuildWorker.csproj --locked-mode \
    && /usr/share/dotnet/dotnet publish dotnet/apps/Weft.BuildWorker/Weft.BuildWorker.csproj -c Release --no-restore --output /out -p:UseSharedCompilation=false

# No inherited SDK-image environment, credentials, shell, network tools or writable host mounts.
# The runner supplies one private bounded noexec tmpfs, and overrides the entrypoint for preview.
FROM scratch
COPY --from=native / /
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
COPY --from=sdk /usr/lib/x86_64-linux-gnu /usr/lib/x86_64-linux-gnu
COPY --from=sdk /lib/x86_64-linux-gnu /lib/x86_64-linux-gnu
COPY --from=sdk /lib64 /lib64
COPY --from=sdk /usr/lib64 /usr/lib64
COPY --from=sdk /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY --from=sdk /out/ /
COPY --from=sdk /etc/os-release /etc/os-release
USER 65532:65532
ENTRYPOINT ["/weft-build-worker"]
