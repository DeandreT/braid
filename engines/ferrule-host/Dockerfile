# This image targets linux/amd64. Build from engines/ferrule-host.
FROM ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b AS build

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
    --default-host x86_64-unknown-linux-gnu --default-toolchain "$RUSTUP_TOOLCHAIN" --component clippy
rustup set auto-self-update disable
test "$(rustc --version)" = 'rustc 1.98.1 (48a229cea 2026-09-01)'
rustup component list --toolchain "$RUSTUP_TOOLCHAIN" --installed | grep -Fx 'clippy-x86_64-unknown-linux-gnu'
cargo clippy --version
rm -f /tmp/rustup-init /tmp/weft-build-ca.pem
EOF

WORKDIR /work
COPY . .

ENV CARGO_NET_GIT_FETCH_WITH_CLI=true

RUN --mount=type=secret,id=proxy_ca <<'EOF'
set -eu
if [ -s /run/secrets/proxy_ca ]; then
    cat /etc/ssl/certs/ca-certificates.crt /run/secrets/proxy_ca > /tmp/weft-build-ca.pem
    export CARGO_HTTP_CAINFO=/tmp/weft-build-ca.pem
    export CURL_CA_BUNDLE=/tmp/weft-build-ca.pem
    export GIT_SSL_CAINFO=/tmp/weft-build-ca.pem
fi
cargo build --release --locked --package weft-ferrule-host --bin weft-ferrule-host
rm -f /tmp/weft-build-ca.pem
EOF

# Assemble only the worker and its observed, trusted GNU runtime dependencies.
# Unexpected dependencies fail the build instead of widening the runtime image.
RUN <<'EOF'
set -eu
install -D -m 0555 /opt/weft-target/release/weft-ferrule-host /runtime/ferrule-host
ldd /runtime/ferrule-host > /tmp/weft-ldd.txt
awk '
    NF == 0 || $1 == "linux-vdso.so.1" { next }
    $2 == "=>" && $3 ~ /^\// { print $3; next }
    $1 ~ /^\// { print $1; next }
    { print "Unexpected worker runtime dependency: " $0 > "/dev/stderr"; exit 1 }
' /tmp/weft-ldd.txt > /tmp/weft-runtime-libraries.txt
test -s /tmp/weft-runtime-libraries.txt
while IFS= read -r library; do
    case "$library" in
        /lib64/ld-linux-x86-64.so.2|/usr/lib64/ld-linux-x86-64.so.2|\
        /lib/x86_64-linux-gnu/ld-linux-x86-64.so.2|/usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2|\
        /lib/x86_64-linux-gnu/libc.so.6|/usr/lib/x86_64-linux-gnu/libc.so.6|\
        /lib/x86_64-linux-gnu/libm.so.6|/usr/lib/x86_64-linux-gnu/libm.so.6|\
        /lib/x86_64-linux-gnu/libgcc_s.so.1|/usr/lib/x86_64-linux-gnu/libgcc_s.so.1)
            ;;
        *)
            printf 'Unadmitted worker runtime library: %s\n' "$library" >&2
            exit 1
            ;;
    esac
    case "$(readlink -f "$library")" in
        /usr/lib/x86_64-linux-gnu/ld-linux-x86-64.so.2|\
        /usr/lib/x86_64-linux-gnu/libc.so.6|\
        /usr/lib/x86_64-linux-gnu/libm.so.6|\
        /usr/lib/x86_64-linux-gnu/libgcc_s.so.1)
            ;;
        *)
            printf 'Worker runtime library resolves outside trusted system paths: %s\n' "$library" >&2
            exit 1
            ;;
    esac
    cp --dereference --parents "$library" /runtime/
done < /tmp/weft-runtime-libraries.txt
install -D -m 0444 /usr/share/doc/libc6/copyright \
    /runtime/usr/share/weft-ferrule-host/runtime/libc6-copyright
install -D -m 0444 /usr/share/doc/libgcc-s1/copyright \
    /runtime/usr/share/weft-ferrule-host/runtime/libgcc-s1-copyright
install -D -m 0444 \
    "$RUSTUP_HOME/toolchains/$RUSTUP_TOOLCHAIN-x86_64-unknown-linux-gnu/share/doc/rust/COPYRIGHT-library.html" \
    /runtime/usr/share/weft-ferrule-host/runtime/rust-std-COPYRIGHT.html
cp -R /work/third-party /runtime/usr/share/weft-ferrule-host/third-party
EOF

FROM build AS qualification
RUN cargo test --release --locked --package weft-ferrule-host --lib --test build_qualification --test protocol_qualification --test preview_qualification --test edit_qualification --test sandbox_qualification --test interchange_qualification --test csv_documents

FROM scratch
ARG WEFT_REVISION=""
LABEL org.opencontainers.image.title="Weft Ferrule worker" \
      org.opencontainers.image.source="https://github.com/DeandreT/weft" \
      org.opencontainers.image.revision="$WEFT_REVISION" \
      io.weft.ferrule.revision="dddc4dce6c93f4137fb2aa2a011360d089e0a51b" \
      io.weft.rust.toolchain="1.98.1"
COPY --from=build /runtime/ /
USER 65532:65532
ENTRYPOINT ["/ferrule-host"]
