#!/bin/bash
set -euo pipefail

# Set defaults if these environment vars aren't present:
FMWK="${AMBROSIA_DOTNET_FRAMEWORK:-netcoreapp2.0}"
CONF="${AMBROSIA_DOTNET_CONF:-Release}"

DEST=CodeGenDependencies/$FMWK
rm -rf $DEST
mkdir -p $DEST

if ! which AmbrosiaCS 2>/dev/null; then
    echo "ERROR: AmbrosiaCS not on PATH"
    exit 1
fi
RSRC=$(dirname `which AmbrosiaCS`)/resources
if ! [ -d "$RSRC" ]; then
    echo "Error: directory does not exist: $RSRC"
    echo "Expected to find resource/ directory which is part of the AMBROSIA binary distribution."
    exit 1
fi

# Baseline CodeGen dependencies:
cp -f "$RSRC/MinimalCodeGenDeps.csproj" $DEST/AmbrosiaCS.csproj
# ^ TODO/FIXME the name should change from AmbrosiaCS.csproj

echo
echo "(STEP 1) Build enough so that we have compiled versions of our RPC interfaces"
BUILDIT="dotnet publish -o publish -c $CONF -f $FMWK "
set -x
$BUILDIT IClient1/IClient1.csproj
$BUILDIT IClient2/IClient2.csproj
$BUILDIT ServerAPI/IServer.csproj
set +x

echo
echo "(STEP 2) Use those DLL's to generate proxy code for RPC calls"

CG="AmbrosiaCS CodeGen -f $FMWK -b=$DEST"
set -x
$CG -o ServerInterfaces  -a ServerAPI/publish/IServer.dll 
$CG -o Client1Interfaces -a ServerAPI/publish/IServer.dll -a IClient1/publish/IClient1.dll 
$CG -o Client2Interfaces -a ServerAPI/publish/IServer.dll -a IClient2/publish/IClient2.dll
set +x

echo
echo "(STEP 3) Now the entire solution can be built."
$BUILDIT HelloWorld.sln
echo
echo "Hello world built."
