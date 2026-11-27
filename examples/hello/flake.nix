{
  description = "narwhal example: a tiny HTTP service plus a prebuilt sidecar";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    narwhal.url = "path:../..";
  };

  outputs =
    {
      self,
      nixpkgs,
      narwhal,
    }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAll =
        f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system} narwhal.lib.${system});
    in
    {
      narwhal.images = forAll (
        pkgs: nlib: {
          web = nlib.mkHttpImage {
            pkgs = pkgs;
            name = "narwhal-example/web";
            tag = "latest";
            message = "hello from narwhal + nix + podman";
            port = 8080;
          };
        }
      );
    };
}
