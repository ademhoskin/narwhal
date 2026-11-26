{ lib
, buildDotnetModule
, dotnet-sdk_11
, clang
, zlib
, stdenv
, oras
, makeWrapper
}:

let
  # Only the sources the CLI needs; keeps the store copy small and avoids
  # copying other projects' bin/obj trees.
  src = lib.cleanSourceWith {
    src = ./..;
    filter = path: type:
      let base = baseNameOf (toString path); in
      !(builtins.elem base [
        ".git"
        ".nuget"
        ".dotnet-home"
        "artifacts"
        "out"
        "bin"
        "obj"
        "result"
      ]);
  };

  runtimeId = if stdenv.hostPlatform.isAarch64 then "linux-arm64" else "linux-x64";
in
buildDotnetModule {
  pname = "narwhal";
  version = "0.1.0";
  inherit src;

  projectFile = "src/Narwhal/Narwhal.csproj";
  nugetDeps = ./deps.json;

  dotnet-sdk = dotnet-sdk_11;
  dotnet-runtime = null;

  # Native AOT: self-contained, no runtime needed at run time. The nixpkgs SDK
  # ships the ILCompiler runtime pack, so no extra NuGet closure is required.
  selfContainedBuild = true;
  inherit runtimeId;
  dotnetFlags = [ "-p:PublishAot=true" ];

  nativeBuildInputs = [ clang makeWrapper ];
  buildInputs = [ zlib ];

  dontStrip = false;

  # An empty list stops dotnetFixupPhase from generating managed wrappers.
  executables = [ ];

  # Override the default install: it copies a framework-dependent apphost and
  # generates a wrapper, neither of which applies to a native AOT binary.
  installPhase = ''
    runHook preInstall

    mkdir -p "$out/bin" "$out/lib/narwhal"
    dotnet publish src/Narwhal/Narwhal.csproj \
      -c Release \
      -r ${runtimeId} \
      --no-restore \
      -p:PublishAot=true \
      -o "$out/lib/narwhal"

    # narwhald and narwhal-guard are the same binary; the code picks its mode
    # from the basename of /proc/self/exe, so the raw copies keep those names.
    for name in narwhal narwhald narwhal-guard; do
      if [ "$name" != narwhal ]; then
        ln -f "$out/lib/narwhal/narwhal" "$out/lib/narwhal/$name"
      fi
      makeWrapper "$out/lib/narwhal/$name" "$out/bin/$name" \
        --prefix PATH : ${lib.makeBinPath [ oras ]}
    done

    runHook postInstall
  '';

  meta = {
    description = "Reproducible containerized builds and deployment with Nix flakes and podman";
    homepage = "https://github.com/ademhoskin/narwhal";
    license = lib.licenses.mit;
    mainProgram = "narwhal";
    platforms = lib.platforms.linux;
  };
}
