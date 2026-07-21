set shell := ["zsh", "-cu"]

default: check

build:
    dotnet build NetCats.slnx --disable-build-servers --verbosity minimal -m:1

test:
    dotnet test NetCats.slnx --disable-build-servers --verbosity minimal -m:1

pocs:
    dotnet test pocs/NetCats.Pocs.Tests/NetCats.Pocs.Tests.csproj --disable-build-servers --verbosity minimal -m:1

examples-build:
    dotnet build examples/PurrfectSeat/NetCats.Examples.PurrfectSeat.slnx --disable-build-servers --verbosity minimal -m:1

examples-test:
    dotnet test examples/PurrfectSeat/NetCats.Examples.PurrfectSeat.slnx --disable-build-servers --verbosity minimal -m:1

examples-run:
    ASPNETCORE_ENVIRONMENT=Demo dotnet run --project examples/PurrfectSeat/src/PurrfectSeat.Api/PurrfectSeat.Api.csproj --no-launch-profile

examples-wasm-build:
    dotnet build examples/PurrfectSeat/src/PurrfectSeat.Wasm/PurrfectSeat.Wasm.csproj --disable-build-servers --verbosity minimal -m:1

examples-wasm-run:
    dotnet run --project examples/PurrfectSeat/src/PurrfectSeat.Wasm/PurrfectSeat.Wasm.csproj --no-launch-profile

examples-wasm-publish:
    dotnet publish examples/PurrfectSeat/src/PurrfectSeat.Wasm/PurrfectSeat.Wasm.csproj --configuration Release --disable-build-servers --verbosity minimal -m:1

mutualgpu-test:
    dotnet test examples/MutualGPU/NetCats.Examples.MutualGPU.slnx --disable-build-servers --verbosity minimal -m:1
    node --test examples/MutualGPU/tests/frontend/*.test.mjs
    npm test --prefix examples/MutualGPU/sdk/typescript

# Executes the checked-in browser SDK integration module in real Chromium.
# Provide MUTUALGPU_PROVIDER_KEY from the demo key file. The test directly uses
# the SDK's provider enrollment mapping, but does not mint a key or open the
# password-gated site enrollment flow.
examples-test-webgpu-enrollment:
    @test -n "${MUTUALGPU_PROVIDER_KEY:-}" || { echo "MUTUALGPU_PROVIDER_KEY is required." >&2; exit 2; }
    MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" MUTUALGPU_PROVIDER_KEY="$MUTUALGPU_PROVIDER_KEY" MUTUALGPU_BROWSER_EXECUTABLE="${MUTUALGPU_BROWSER_EXECUTABLE:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}" node --test examples/MutualGPU/sdk/typescript/integration/browser-wss-enrollment.test.mjs

# Executes a real Chromium fetch from the hosted Hugging Face Space to the public
# API with credentials: include. Override MUTUALGPU_CORS_TEST_ORIGIN for another
# explicitly allowed provider origin.
examples-test-cross-origin-cors:
    MUTUALGPU_API_URL="${MUTUALGPU_API_URL:-https://mutualgpu.com}" MUTUALGPU_CORS_TEST_ORIGIN="${MUTUALGPU_CORS_TEST_ORIGIN:-https://yosun-triposplat-webgpu-demo.static.hf.space}" MUTUALGPU_BROWSER_EXECUTABLE="${MUTUALGPU_BROWSER_EXECUTABLE:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}" node --test examples/MutualGPU/sdk/typescript/integration/browser-cross-origin-cors.test.mjs

mutualgpu-dev-cert:
    dotnet dev-certs https --trust

mutualgpu-local-smoke:
    ./examples/MutualGPU/scripts/run-local-composition.sh smoke

mutualgpu-local-demo:
    ./examples/MutualGPU/scripts/run-local-composition.sh demo

check: build test examples-build examples-test examples-wasm-build
