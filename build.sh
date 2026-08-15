#!/bin/bash
set -e

# Install .NET SDK if not already available
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET SDK..."
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$HOME/dotnet-install.sh"
    bash "$HOME/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
    export PATH="$HOME/.dotnet:$PATH"
fi

# Verify .NET is available
dotnet --version

# Publish the Blazor WebAssembly app
dotnet publish PickleStacking/PickleStacking.csproj -c Release -o dist

echo "Build complete. Output in dist/wwwroot"