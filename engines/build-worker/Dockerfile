# Build from the repository root, after building the pinned native worker from this source.
# Use a registry digest, or the image-ID-bound local reference supplied by build_image.py.
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
RUN /usr/share/dotnet/dotnet restore dotnet/apps/Weft.CompiledRuntime/Weft.CompiledRuntime.csproj --locked-mode \
    && /usr/share/dotnet/dotnet publish dotnet/apps/Weft.CompiledRuntime/Weft.CompiledRuntime.csproj -c Release --no-restore --output /runtime -p:UseSharedCompilation=false

# Optional qualification client; it is not shipped in the compiler image.
FROM sdk AS qualification
COPY migrations migrations
COPY engines/ferrule-host/tests/fixtures engines/ferrule-host/tests/fixtures
RUN /usr/share/dotnet/dotnet restore dotnet/tests/Weft.Tests/Weft.Tests.csproj --locked-mode --source https://api.nuget.org/v3/index.json -m:1 \
    && /usr/share/dotnet/dotnet build dotnet/tests/Weft.Tests/Weft.Tests.csproj -c Release --no-restore -p:OpenApiGenerateDocuments=false -p:UseSharedCompilation=false -m:1

# No inherited SDK-image environment, credentials, shell, network tools or writable host mounts.
# The runner supplies one private bounded noexec tmpfs, and overrides the entrypoint for preview.
FROM sdk AS checked-native
ARG WEFT_NATIVE_BINARY_SHA256
COPY --from=native /ferrule-host /verified-native/ferrule-host
RUN test "${#WEFT_NATIVE_BINARY_SHA256}" = 64 \
    && echo "${WEFT_NATIVE_BINARY_SHA256}  /verified-native/ferrule-host" | sha256sum --check --status

FROM scratch
COPY --from=native / /
COPY --from=checked-native /verified-native/ferrule-host /ferrule-host
COPY --from=sdk /usr/share/dotnet /usr/share/dotnet
COPY --from=sdk /usr/lib/x86_64-linux-gnu /usr/lib/x86_64-linux-gnu
COPY --from=sdk /lib/x86_64-linux-gnu /lib/x86_64-linux-gnu
COPY --from=sdk /lib64 /lib64
COPY --from=sdk /usr/lib64 /usr/lib64
COPY --from=sdk /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY --from=sdk /out/ /
COPY --from=sdk /runtime/ /
COPY --from=sdk /etc/os-release /etc/os-release
USER 65532:65532
ENTRYPOINT ["/weft-build-worker"]
