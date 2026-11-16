# Build from the repository root. The toolchain is identical to the native worker.
FROM ubuntu@sha256:f610ab94648195aa356059f5b41d6085c9d4d903c072430cdd1af7bdb646106b AS build
ENV CARGO_HOME=/opt/cargo RUSTUP_HOME=/opt/rustup RUSTUP_TOOLCHAIN=1.98.1 \
    CARGO_TARGET_DIR=/opt/braid-target CARGO_BUILD_JOBS=2 CARGO_INCREMENTAL=0 \
    RUSTUP_USE_CURL=1 PATH=/opt/cargo/bin:$PATH CARGO_NET_GIT_FETCH_WITH_CLI=true
RUN apt-get update && apt-get install --no-install-recommends -y build-essential ca-certificates curl git python3 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /work
COPY engines/ferrule-host engines/ferrule-host
COPY engines/ferrule-edit-wasm engines/ferrule-edit-wasm
COPY tools/edit-core tools/edit-core
RUN python3 tools/edit-core/build.py --install --destination /package/edit --manifest /package/edit-assets.generated.ts
FROM scratch
COPY --from=build /package/ /
