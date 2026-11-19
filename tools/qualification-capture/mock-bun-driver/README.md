# Bun mock capture driver

This fixture is a repository-owned consumer of the capture driver's `--output` argument. For a standalone check, run `bun test ./driver.test.mjs` here. The CLI requires an absolute path to a fresh, user-owned `0700` directory and `BRAID_MOCK_CAPTURE=1`. It refuses root and writes `bun-mock.json` exclusively. A synthetic receipt is not a qualification result.

One CLI process issues one four-byte query to a dedicated JS worker. The main thread checks its own monotonic deadline before accepting a reply and alone writes the result. The hold case models a worker that never replies; rejecting that request is not proof of interrupting or joining a blocked native call.

The capture owner applies its 64 MiB per-file limit to the driver executable as well as other trusted files. A larger Bun executable is refused during preflight with `memberLimit`, before the driver starts or a capture directory is created. Standalone fixture success does not establish end-to-end capture admission. A wrapper or manifest is not a substitute for admitting the executable itself.

The only descriptor admitted is for a regular fixture file. The owner checks the nonnegative signed-27-bit range before use, checks the open file and hashes its bytes. The expected digest binds the checked-in mock ABI description: one little-endian unsigned 32-bit input and output. No descriptor is handed to Bun's socket backend and no dynamic library is opened. Native qualification still needs an independent proof covering every pre-narrowing descriptor path, a pinned caller/library pair with pre-load provenance and ABI evidence, and independent design and host-fit review under #401.
