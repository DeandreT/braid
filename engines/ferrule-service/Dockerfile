# The portable Ferrule service image. Build from the repository root after building the pinned native worker
# image from engines/ferrule-host:
#   docker build --file engines/ferrule-service/Dockerfile \
#     --build-arg WEFT_NATIVE_IMAGE=<native image reference> \
#     --build-arg WEFT_NATIVE_BINARY_SHA256=<sha256 of its /ferrule-host> .
# Use a registry digest for the native image, or a unique local tag bound to the verified image ID. The service's
# build identity is sha256:<WEFT_NATIVE_BINARY_SHA256>; it is recorded in the io.weft.ferrule.worker-sha256 label.
# See docs/ferrule-service.md.
ARG WEFT_NATIVE_IMAGE
FROM ${WEFT_NATIVE_IMAGE} AS native

FROM ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b AS sdk
ARG DOTNET_SDK_VERSION=10.0.112
ARG DOTNET_SDK_SHA512=1ac949dfd3929855e3a102a5b12f843967a784ad2d911dfcfff6abdf81a2e78bccf25ddc055547de02b36133bf792aaf97fcba8db8aa6b52ac2e018b86935d05
RUN apt-get update && apt-get install --yes --no-install-recommends ca-certificates curl libstdc++6 zlib1g libssl3t64 \
    && mkdir -p /usr/share/dotnet \
    && curl --fail --silent --show-error --location --retry 3 --output /tmp/sdk.tar.gz \
       "https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-x64.tar.gz" \
    && echo "${DOTNET_SDK_SHA512}  /tmp/sdk.tar.gz" | sha512sum --check --status \
    && tar -xzf /tmp/sdk.tar.gz -C /usr/share/dotnet && rm /tmp/sdk.tar.gz \
    && rm -rf /var/lib/apt/lists/*
ENV DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
WORKDIR /src
COPY global.json ./
COPY dotnet dotnet
RUN /usr/share/dotnet/dotnet restore dotnet/apps/Weft.FerruleService/Weft.FerruleService.csproj --locked-mode \
    && /usr/share/dotnet/dotnet publish dotnet/apps/Weft.FerruleService/Weft.FerruleService.csproj -c Release --no-restore \
       --output /out/app -p:UseSharedCompilation=false \
    && install -d -m 1777 /out/tmp

# The worker binary must be exactly the qualified native build.
FROM sdk AS checked-native
ARG WEFT_NATIVE_BINARY_SHA256
COPY --from=native /ferrule-host /verified-native/ferrule-host
RUN test "${#WEFT_NATIVE_BINARY_SHA256}" = 64 \
    && echo "${WEFT_NATIVE_BINARY_SHA256}  /verified-native/ferrule-host" | sha256sum --check --status

# No shell, package manager, SDK or network tools: the service, the verified worker, the .NET and ASP.NET Core
# shared frameworks and their libraries. Mount a writable volume at /tmp; the root stays read-only.
FROM scratch
ARG WEFT_REVISION=""
ARG WEFT_NATIVE_BINARY_SHA256
LABEL org.opencontainers.image.title="Weft Ferrule service" \
      org.opencontainers.image.source="https://github.com/DeandreT/weft" \
      org.opencontainers.image.revision="$WEFT_REVISION" \
      io.weft.ferrule.revision="577359a991a7dd44172a3c4af91fdcab7592457c" \
      io.weft.ferrule.worker-sha256="$WEFT_NATIVE_BINARY_SHA256"
COPY --from=native / /
COPY --from=checked-native /verified-native/ferrule-host /ferrule-host
# The native layer already holds the dynamic loader at /lib64; the service adds the shared libraries .NET needs.
COPY --from=sdk /usr/lib/x86_64-linux-gnu /usr/lib/x86_64-linux-gnu
COPY --from=sdk /usr/lib/ssl /usr/lib/ssl
COPY --from=sdk /etc/ssl/openssl.cnf /etc/ssl/openssl.cnf
COPY --from=sdk /usr/share/dotnet/dotnet /usr/share/dotnet/dotnet
COPY --from=sdk /usr/share/dotnet/host /usr/share/dotnet/host
COPY --from=sdk /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY --from=sdk /usr/share/dotnet/shared/Microsoft.AspNetCore.App /usr/share/dotnet/shared/Microsoft.AspNetCore.App
COPY --from=sdk /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt /usr/share/dotnet/
COPY --from=sdk /etc/os-release /etc/os-release
COPY --from=sdk /out/app /app
COPY --from=sdk /out/tmp /tmp
ENV DOTNET_ROOT=/usr/share/dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    WEFT_FERRULE_SERVICE_WORKER_EXECUTABLE=/ferrule-host WEFT_FERRULE_SERVICE_TEMPORARY_ROOT=/tmp
USER 65532:65532
WORKDIR /app
EXPOSE 8443 8080
ENTRYPOINT ["/app/weft-ferrule-service"]
