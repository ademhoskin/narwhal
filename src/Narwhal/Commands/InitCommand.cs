using Narwhal.Cli;

namespace Narwhal.Commands;

/// <summary><c>narwhal init</c>: scaffold a manifest and the flake that builds its images.</summary>
public static class InitCommand
{
    public const string DefaultNarwhalRef = "github:ademhoskin/narwhal";

    public static int Run(CliOptions options, Reporter reporter)
    {
        var baseDirectory = options.Directory is null
            ? Environment.CurrentDirectory
            : Path.GetFullPath(options.Directory);

        var target = options.Positionals.Count > 0
            ? Path.GetFullPath(Path.Combine(baseDirectory, options.Positionals[0]))
            : baseDirectory;

        var name = options.Value("name") ?? SanitizeName(Path.GetFileName(target.TrimEnd('/')));
        var narwhalRef = options.Value("narwhal-ref") ?? DefaultNarwhalRef;
        var force = options.Flag("force");

        Directory.CreateDirectory(target);

        var manifestPath = Path.Combine(target, "narwhal.toml");
        var flakePath = Path.Combine(target, "flake.nix");
        var gitignorePath = Path.Combine(target, ".gitignore");

        if (!force && (File.Exists(manifestPath) || File.Exists(flakePath)))
            throw new UsageException($"{target} already has narwhal.toml or flake.nix; pass --force to overwrite");

        File.WriteAllText(manifestPath, ManifestTemplate(name));
        File.WriteAllText(flakePath, FlakeTemplate(name, narwhalRef));

        if (!File.Exists(gitignorePath))
            File.WriteAllText(gitignorePath, "result\nresult-*\n");
        else if (!File.ReadAllText(gitignorePath).Contains("result", StringComparison.Ordinal))
            File.AppendAllText(gitignorePath, "\nresult\nresult-*\n");

        reporter.Ok($"wrote {Path.GetRelativePath(Environment.CurrentDirectory, manifestPath)}");
        reporter.Ok($"wrote {Path.GetRelativePath(Environment.CurrentDirectory, flakePath)}");
        reporter.Info("");
        reporter.Info("next steps:");
        reporter.Info("  1. nix flake lock            # pin nixpkgs and narwhal");
        reporter.Info("  2. narwhal build             # build images, load into podman, write narwhal.lock.json");
        reporter.Info("  3. narwhal up                # start the service container");
        reporter.Info("  4. narwhal ps                # check it is running");
        reporter.Info("");
        reporter.Info("commit narwhal.lock.json and flake.lock: together they pin every build input.");
        return 0;
    }

    private static string ManifestTemplate(string name) => $$"""
        # narwhal.toml - a service container: the services narwhal builds and runs.
        #
        # Images come from the flake below (narwhal.images.<service> by default),
        # so every deploy is built from pinned Nix inputs.

        name = "{{name}}"
        flake = "."

        [build]
        attr_prefix = "narwhal.images"

        [deploy]
        restart = "unless-stopped"
        # network = "narwhal-{{name}}"

        [[service]]
        name = "hello"
        description = "a minimal service so the first build succeeds"
        image = "{{name}}/hello"
        tag = "latest"
        ports = ["8080:8080"]
        healthcheck = "true"

        # [[service]]
        # name = "api"
        # image = "{{name}}/api"
        # ports = ["3000:3000"]
        # depends_on = ["db"]
        # env = { LOG_LEVEL = "info" }
        #
        # [[service]]
        # name = "db"
        # prebuilt = true                       # use a registry image instead of nix
        # image = "docker.io/library/postgres"
        # tag = "17-alpine"
        # volumes = ["{{name}}-db:/var/lib/postgresql/data"]
        # env = { POSTGRES_DB = "app", POSTGRES_USER = "app", POSTGRES_PASSWORD = "app" }
        """;

    private static string FlakeTemplate(string name, string narwhalRef) => $$"""
        {
          description = "{{name}} - container images built reproducibly with narwhal";

          inputs = {
            nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
            narwhal.url = "{{narwhalRef}}";
          };

          outputs = { self, nixpkgs, narwhal }:
            let
              systems = [ "x86_64-linux" "aarch64-linux" ];
              forAll = f:
                nixpkgs.lib.genAttrs systems
                  (system: f nixpkgs.legacyPackages.${system} narwhal.lib.${system});
            in
            {
              # `narwhal build` looks up narwhal.images.<system>.<service>.
              # Add one attribute per service in narwhal.toml.
              narwhal.images = forAll (pkgs: nlib: {
                hello = nlib.mkHelloImage {
                  pkgs = pkgs;
                  name = "{{name}}/hello";
                  message = "hello from {{name}}";
                };

                # api = nlib.mkImage {
                #   pkgs = pkgs;                       # keeps config paths consistent
                #   name = "{{name}}/api";
                #   tag = "latest";
                #   contents = [ pkgs.busybox ];
                #   config.Cmd = [ "sh" "-c" "echo api up; exec sleep infinity" ];
                # };
              });
            };
        }
        """;

    private static string SanitizeName(string raw)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                builder.Append(c);
            else
                builder.Append('-');
        }
        var value = builder.ToString().Trim('-', '.', '_');
        return value.Length == 0 ? "narwhal" : value;
    }
}
