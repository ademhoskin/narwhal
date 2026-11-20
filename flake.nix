{
  description = "narwhal - reproducible container builds and deployment with Nix flakes and podman";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  };

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
      devSystems = [ "x86_64-linux" "aarch64-linux" ];
      forDevSystems = f: nixpkgs.lib.genAttrs devSystems (system: f nixpkgs.legacyPackages.${system});
    in
    {
      # Helpers for defining container images in a user's flake:
      #   narwhal.lib.${system}.mkImage { pkgs = ...; ... }
      lib = forAllSystems (pkgs: import ./nix/lib.nix { inherit (pkgs) lib; });

      packages = nixpkgs.lib.genAttrs [ "x86_64-linux" "aarch64-linux" ] (system:
        let
          pkgs = nixpkgs.legacyPackages.${system};
        in
        {
          default = pkgs.callPackage ./nix/package.nix { };
          narwhal = pkgs.callPackage ./nix/package.nix { };
        });

      devShells = forDevSystems (pkgs: {
        default = pkgs.mkShell {
          name = "narwhal";
          packages = with pkgs; [
            dotnet-sdk_11
            clang
            llvm
            binutils
            zlib
            git
            jq
          ];
          env = {
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
            DOTNET_CLI_UI_LANGUAGE = "en-US";
          };
          shellHook = ''
            export NUGET_PACKAGES="$PWD/.nuget/packages"
            echo "narwhal devshell: $(dotnet --version)"
          '';
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixpkgs-fmt);
    };
}
