# The portable Ferrule service image. Build from the repository root after building the pinned native worker
# image from engines/ferrule-host:
#   docker build --file engines/ferrule-service/Dockerfile \
#     --build-arg WEFT_NATIVE_IMAGE=<native image reference> \
#     --build-arg WEFT_NATIVE_BINARY_SHA256=<sha256 of its /ferrule-host> .
# Use a registry digest for the native image, or a unique local tag bound to the verified image ID. The service's
# build identity is sha256:<WEFT_NATIVE_BINARY_SHA256>; it is recorded in the io.weft.ferrule.worker-sha256 label.
# The image also holds the compile launcher (built here from engines/compile-sandbox with the worker's pinned Rust
# toolchain), the pinned Roslyn compiler and the reference assemblies, used only when compilation is enabled.
# Requires BuildKit. See docs/ferrule-service.md.
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
ENV DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=false DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_PROCESSOR_COUNT=2
WORKDIR /src
COPY global.json ./
COPY dotnet dotnet
RUN /usr/share/dotnet/dotnet restore dotnet/apps/Weft.FerruleService/Weft.FerruleService.csproj --locked-mode \
    && /usr/share/dotnet/dotnet publish dotnet/apps/Weft.FerruleService/Weft.FerruleService.csproj -c Release --no-restore \
        --output /out/app -p:UseSharedCompilation=false -p:OpenApiGenerateDocuments=false \
    && /usr/share/dotnet/dotnet restore dotnet/apps/Weft.CompiledRuntime/Weft.CompiledRuntime.csproj --locked-mode \
    && /usr/share/dotnet/dotnet publish dotnet/apps/Weft.CompiledRuntime/Weft.CompiledRuntime.csproj -c Release --no-restore \
        --output /out/runtime -p:UseSharedCompilation=false \
    && install -d -m 1777 /out/tmp

FROM ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b AS launcher

ENV CARGO_HOME=/opt/cargo \
    RUSTUP_HOME=/opt/rustup \
    RUSTUP_TOOLCHAIN=1.98.1 \
    CARGO_TARGET_DIR=/opt/weft-target \
    CARGO_BUILD_JOBS=2 \
    CARGO_INCREMENTAL=0 \
    RUSTUP_USE_CURL=1 \
    PATH=/opt/cargo/bin:$PATH

RUN apt-get update \
    && apt-get install --no-install-recommends -y build-essential ca-certificates curl git \
    && rm -rf /var/lib/apt/lists/*

# The installer digest was verified against the official archived .sha256 file:
# https://static.rust-lang.org/rustup/archive/1.29.1/x86_64-unknown-linux-gnu/rustup-init.sha256
# An optional proxy_ca BuildKit secret adds session trust without retaining it.
RUN --mount=type=secret,id=proxy_ca <<'EOF'
set -eu
if [ -s /run/secrets/proxy_ca ]; then
    cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/weft-build-ca.pem
    export CURL_CA_BUNDLE=/tmp/weft-build-ca.pem
fi
curl --proto '=https' --tlsv1.2 --fail --show-error --location \
    https://static.rust-lang.org/rustup/archive/1.29.1/x86_64-unknown-linux-gnu/rustup-init \
    --output /tmp/rustup-init
printf '%s  %s\n' dda7234360b7f578ca8b0ddcb80145646fa61a67c1720a5abc7051b35c9fcb71 /tmp/rustup-init \
    | sha256sum --check -
chmod 0755 /tmp/rustup-init
/tmp/rustup-init --yes --no-modify-path --profile minimal \
    --default-host x86_64-unknown-linux-gnu --default-toolchain "$RUSTUP_TOOLCHAIN"
rustup set auto-self-update disable
test "$(rustc --version)" = 'rustc 1.98.1 (48a229cea 2026-09-01)'
rm -f /tmp/rustup-init /tmp/weft-build-ca.pem
EOF

WORKDIR /work
COPY engines/compile-sandbox .
# Assemble the launcher with only the trusted GNU runtime libraries the worker also uses.
RUN <<'EOF'
set -eu
cargo build --release --locked --bin weft-compile-sandbox
install -D -m 0555 /opt/weft-target/release/weft-compile-sandbox /out/weft-compile-sandbox
ldd /out/weft-compile-sandbox > /tmp/weft-ldd.txt
awk '
    NF == 0 || $1 == "linux-vdso.so.1" { next }
    $2 == "=>" && $3 ~ /^\// { print $3; next }
    $1 ~ /^\// { print $1; next }
    { print "Unexpected launcher runtime dependency: " $0 > "/dev/stderr"; exit 1 }
' /tmp/weft-ldd.txt > /tmp/weft-runtime-libraries.txt
test -s /tmp/weft-runtime-libraries.txt
while IFS= read -r library; do
    case "$(readlink -f "$library")" in
        /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2|/usr/lib/x86_64-linux-gnu/libc.so.6|\
        /usr/lib/x86_64-linux-gnu/libm.so.6|/usr/lib/x86_64-linux-gnu/libgcc_s.so.1)
            ;;
        *)
            printf 'Unadmitted launcher runtime library: %s\n' "$library" >&2
            exit 1
            ;;
    esac
done < /tmp/weft-runtime-libraries.txt
EOF

# The worker binary must be exactly the qualified native build.
FROM sdk AS checked-native
ARG WEFT_NATIVE_BINARY_SHA256
COPY --from=native /ferrule-host /verified-native/ferrule-host
RUN test "${#WEFT_NATIVE_BINARY_SHA256}" = 64 \
    && echo "${WEFT_NATIVE_BINARY_SHA256}  /verified-native/ferrule-host" | sha256sum --check --status

# No shell, package manager, SDK or network tools: the service, the verified worker, the compile launcher, the .NET
# and ASP.NET Core shared frameworks and their libraries, and of the SDK only the pinned compiler and the reference
# assemblies. Mount a writable volume at /tmp; the root stays read-only.
FROM scratch
ARG WEFT_REVISION=""
ARG WEFT_NATIVE_BINARY_SHA256
LABEL org.opencontainers.image.title="Weft Ferrule service" \
      org.opencontainers.image.source="https://github.com/DeandreT/weft" \
      org.opencontainers.image.revision="$WEFT_REVISION" \
      io.weft.ferrule.revision="dddc4dce6c93f4137fb2aa2a011360d089e0a51b" \
      io.weft.ferrule.worker-sha256="$WEFT_NATIVE_BINARY_SHA256" \
      io.weft.ferrule.compile-sdk="10.0.112"
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
COPY --from=sdk /usr/share/dotnet/sdk/10.0.112/Roslyn/bincore /usr/share/dotnet/sdk/10.0.112/Roslyn/bincore
COPY --from=sdk /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.12 /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/10.0.12
COPY --from=sdk /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt /usr/share/dotnet/
COPY --from=launcher /out/weft-compile-sandbox /weft-compile-sandbox
COPY --from=sdk /etc/os-release /etc/os-release
COPY --from=sdk /out/app /app
COPY --from=sdk /out/runtime /runtime
COPY --from=sdk /out/tmp /tmp
ENV DOTNET_ROOT=/usr/share/dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    WEFT_FERRULE_SERVICE_WORKER_EXECUTABLE=/ferrule-host WEFT_FERRULE_SERVICE_TEMPORARY_ROOT=/tmp
USER 65532:65532
WORKDIR /app
EXPOSE 8443 8080
ENTRYPOINT ["/app/weft-ferrule-service"]
