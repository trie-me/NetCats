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

check: build test examples-build examples-test
