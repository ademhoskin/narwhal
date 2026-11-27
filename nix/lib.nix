# Helpers for building reproducible OCI images with Nix.
#
# `pkgs` is always passed by the caller so that the paths a user puts in
# `config.Cmd` come from the same nixpkgs as the image contents. Passing
# narwhal's own nixpkgs here would silently produce broken entrypoints.
#
# Usage from a user's flake:
#
#   outputs = { self, nixpkgs, narwhal }: {
#     narwhal.images.x86_64-linux.api =
#       narwhal.lib.x86_64-linux.mkImage {
#         pkgs = nixpkgs.legacyPackages.x86_64-linux;
#         name = "acme/api";
#         tag = "latest";
#         contents = [ nixpkgs.legacyPackages.x86_64-linux.busybox ];
#         config.Cmd = [ "sh" "-c" "echo hello; sleep infinity" ];
#       };
#   };
{ lib }:
let
  # Timestamps are pinned to the epoch so identical inputs produce identical
  # image tarballs. Nix's sandbox already does this; being explicit documents
  # the intent and survives impure builds.
  epoch = "1970-01-01T00:00:01Z";
in
rec {
  # Build a layered image from a list of store paths (usually packages).
  mkImage =
    {
      pkgs,
      name,
      tag ? "latest",
      contents ? [ ],
      config ? { },
      maxLayers ? 100,
      created ? epoch,
      extraCommands ? "",
    }:
    pkgs.dockerTools.buildLayeredImage {
      inherit
        name
        tag
        maxLayers
        created
        extraCommands
        ;
      contents = contents;
      config = {
        # Deterministic defaults; callers can override anything.
        Labels = {
          "org.opencontainers.image.title" = name;
          "org.opencontainers.image.created" = created;
          "io.narwhal.managed" = "true";
        }
        // (config.Labels or { });
      }
      // lib.removeAttrs config [ "Labels" ];
    };

  # Convenience wrapper for the common case: one package is the payload.
  mkPackageImage =
    {
      pkgs,
      name,
      tag ? "latest",
      package,
      config ? { },
      maxLayers ? 100,
      created ? epoch,
    }:
    mkImage {
      inherit
        pkgs
        name
        tag
        config
        maxLayers
        created
        ;
      contents = [ package ];
    };

  # A minimal "hello" image, handy for smoke tests and demos.
  mkHelloImage =
    {
      pkgs,
      name ? "narwhal/hello",
      tag ? "latest",
      message ? "hello from narwhal",
    }:
    mkImage {
      inherit pkgs name tag;
      contents = [ pkgs.busybox ];
      config = {
        Cmd = [
          "sh"
          "-c"
          "echo ${lib.escapeShellArg message}; exec sleep infinity"
        ];
        Labels."io.narwhal.hello" = "true";
      };
    };

  # A tiny static HTTP server, used by the bundled example and tests.
  mkHttpImage =
    {
      pkgs,
      name,
      tag ? "latest",
      message ? "hello from narwhal",
      port ? 8080,
    }:
    mkImage {
      inherit pkgs name tag;
      contents = [ pkgs.busybox ];
      extraCommands = ''
        mkdir -p srv
        printf '%s\n' ${lib.escapeShellArg message} > srv/index.html
      '';
      config = {
        Cmd = [
          "busybox"
          "httpd"
          "-f"
          "-p"
          (toString port)
          "-h"
          "/srv"
        ];
        Env = [ "PATH=/bin" ];
        ExposedPorts."${toString port}/tcp" = { };
      };
    };
}
