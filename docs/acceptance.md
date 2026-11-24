# Narwhal CLI acceptance map

Concrete Given/When/Then scenarios for every command, exit code and known failure
path. `Test` names are `tests/Narwhal.Tests/<File>.cs::<Method>`; `GAP` means no
test covers the scenario yet. This is a map, not an essay.

Run: `nix develop -c dotnet test tests/Narwhal.Tests --nologo`

## Exit codes

| Code | Contract | Covered by |
| --- | --- | --- |
| 0 | success, including "nothing to do" paths | `MultiPlatformIntegrationTests.cs::BuildsVerifiesPlansPushesAndRunsEveryConfiguredSystem` (build, plan, verify, up), `PushesWhenTheLockedStorePathIsADirectory` (build, push), `PushLeavesTheTagOnTheHostForEitherSystemOrder` (push) |
| 1 | usage, manifest, or configuration error | `MultiPlatformIntegrationTests.cs::SelectingOneSystemBuildsAndLocksOnlyThatSystem` (unknown `--system`), `MultiPlatformIntegrationTests.cs::UpFailsWhenTheHostIsNotConfigured` (host not configured); parser/loader units cover the messages but not `App.Main` returning 1: missing manifest, invalid TOML, unknown service, unknown flag, missing option value, no command: GAP |
| 2 | build failure (`BuildException`) | GAP (no test drives a failing nix build through `App.Main`) |
| 3 | podman/tool/process failure | `MultiPlatformIntegrationTests.cs::DriftedLockImageIdBlocksPushBeforeAnyPush`, `MultiPlatformIntegrationTests.cs::StaleLockAttrBlocksPush`, `MultiPlatformIntegrationTests.cs::RestoreFailureDoesNotMaskTheOriginalPushFailure`; missing container and missing tool: GAP |
| 4 | reproducibility mismatch | `MultiPlatformIntegrationTests.cs::LockedBuildReportsEveryMissingAndDriftingPlatform`, `MultiPlatformIntegrationTests.cs::VerifyReportsDriftForOnePlatform`; missing lock: GAP |
| 130 | interrupted (Ctrl-C / cancellation) | GAP |

## init

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| fresh scaffold | Given an empty directory; When `narwhal init`; Then exit 0 and `narwhal.toml`, `flake.nix`, `.gitignore` exist | GAP |
| explicit target and name | Given a parent directory; When `narwhal init app --name acme`; Then files land in `app/` with `name = "acme"` | GAP |
| refuse overwrite | Given an existing `narwhal.toml`; When `narwhal init`; Then exit 1 mentioning `--force` | GAP |
| force overwrite | Given an existing manifest; When `narwhal init --force`; Then exit 0 and the template replaces the file | GAP |
| custom narwhal ref | Given `--narwhal-ref github:owner/repo`; When init; Then `flake.nix` contains that input URL | GAP |
| derived name | Given a directory such as `My App`; When init; Then the project name is sanitised to `my-app` | GAP |

## plan

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| human table | Given a manifest and lock; When `narwhal plan`; Then one row per service/system shows attr, image, status and digest | GAP |
| status values | Given no lock / a lock missing a platform / a stale attr / a matching lock; When plan; Then status is `new` / `missing-platform` / `stale` / `locked` | GAP |
| JSON shape | Given two systems; When `plan --json`; Then `project`, `manifest`, `lock`, `lock_stale`, `systems[]`, `services[].platforms.<system>{platform,attr,status,digest,...}` are present | `MultiPlatformIntegrationTests.cs::BuildsVerifiesPlansPushesAndRunsEveryConfiguredSystem` |
| prebuilt row | Given a prebuilt service; When plan; Then its row says `prebuilt` and has no platform entries | GAP |
| dependency order | Given `web -> api -> db`; When plan; Then rows are ordered `db`, `api`, `web` | `ManifestTests.cs::OrdersDependenciesBeforeDependents` (ordering primitive only) |
| stale manifest warning | Given a lock whose manifest hash differs; When plan; Then a `warn` names the manifest and suggests `narwhal build` | GAP |
| no lock | Given no lock file; When plan; Then exit 0 and a note that build pins hashes | GAP |
| refresh | Given `--refresh`; When plan; Then each distinct flake is queried with `nix flake metadata` and shown | GAP |

## build

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| build every service/system | Given a manifest with two systems; When `narwhal build`; Then both archive ids are locked, only the host archive is loaded, exit 0 | `MultiPlatformIntegrationTests.cs::BuildsVerifiesPlansPushesAndRunsEveryConfiguredSystem` |
| selected system | Given two systems; When `build --system aarch64-linux`; Then only that attr is built and only that platform is locked | `MultiPlatformIntegrationTests.cs::SelectingOneSystemBuildsAndLocksOnlyThatSystem` |
| unknown system | Given `--system mips-linux`; When build; Then exit 1 and the configured systems are named | `MultiPlatformIntegrationTests.cs::SelectingOneSystemBuildsAndLocksOnlyThatSystem` |
| locked drift | Given a lock missing one platform and a drifted nar hash; When `build --locked`; Then exit 4 and both failures are listed | `MultiPlatformIntegrationTests.cs::LockedBuildReportsEveryMissingAndDriftingPlatform` |
| locked success | Given a complete matching lock; When `build --locked`; Then exit 0 without rewriting the lock | GAP |
| build failure | Given nix exits non-zero; When build; Then exit 2, `BuildException` detail is indented, and no lock is written | GAP |
| no buildable services | Given only prebuilt services; When build; Then exit 0 with "no buildable services" | GAP |
| prebuilt filtering | Given a mix of prebuilt and buildable services; When build; Then nix is only invoked for the buildable ones | GAP |
| directory store artifact | Given nix reports a directory containing `tarball`; When build/push; Then `ResolveArtifact` finds the archive | `MultiPlatformIntegrationTests.cs::PushesWhenTheLockedStorePathIsADirectory` |
| lock covers all systems | Given a build for a subset of systems; When the lock is written; Then `manifest_sha256` is cleared | `MultiPlatformIntegrationTests.cs::IncompleteCoverageClearsTheManifestHash` |
| migration write-back | Given a v1 lock; When build/up reuses it; Then a v2 lock is written | `MultiPlatformIntegrationTests.cs::ReusableUpPersistsAVersionOneMigration`, `LockFileTests.cs::MigratesVersionOneUsingTheAttrSystem` |
| --no-load | Given `--no-load`; When build; Then archives are not handed to podman | `MultiPlatformIntegrationTests.cs::LockedBuildReportsEveryMissingAndDriftingPlatform` (flag accepted), full assertion: GAP |
| --rebuild | Given `--rebuild`; When build; Then nix receives `--rebuild` and the lock is refreshed | GAP |
| lock write is atomic | Given a killed process mid-save; When the lock is read; Then no partial JSON is visible | GAP (`LockFileTests.cs::SavesAndLoadsVersionTwoDeterministically` covers only the happy path) |

## up / deploy

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| deploy alias | Given `narwhal deploy`; When parsed and looked up; Then it is accepted and resolves to a command spec | `CommandLineTests.cs::MapsDeployToUp` (parser only; dispatch to `up`: GAP) |
| host not configured | Given `build.systems` excludes the host; When `up`; Then exit 1 names the host and the fix | `MultiPlatformIntegrationTests.cs::UpFailsWhenTheHostIsNotConfigured` |
| dependency closure | Given `up api` and `api` depends on `db`; When up; Then `db` is started too, dependencies first | GAP |
| network and volumes | Given named volumes; When up; Then the project network and named volumes exist before `run` | GAP |
| health wait | Given a healthcheck; When up; Then it polls health and fails on `unhealthy` or timeout | GAP |
| --no-build | Given an unloaded image; When `up --no-build`; Then exit 3 naming the missing image; with the image loaded it starts | GAP |
| conflicting flags | Given `up --build --no-build`; When up; Then exit 1 | GAP |
| prebuilt pull | Given a prebuilt service whose image is absent; When up; Then podman pulls it once | GAP |
| reuse host lock | Given a matching host entry whose store path exists; When up; Then the archive is reused instead of rebuilt | GAP (only the resulting lock write is covered) |
| --wait bounds | Given `--wait -1` or `--wait 99999`; When up; Then exit 1 before touching podman | GAP |
| JSON result | Given `up --json`; When up; Then the trailing `ps` JSON shape is emitted | GAP |

## down

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| stop containers | Given running project containers; When `down`; Then each is removed and a count is reported | GAP |
| keep volumes | Given `down` without `--volumes`; Then volumes survive | GAP |
| remove volumes | Given `down --volumes`; Then project volumes are removed and counted | GAP |
| nothing running | Given no containers; When down; Then exit 0 with "nothing to stop" | GAP |
| JSON shape | Given `down --json`; Then `{project, containers_removed, volumes_removed}` is emitted | GAP |

## ps

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| list states | Given some created and some missing containers; When `ps`; Then every service shows state, health, ports and image, with `not created` as the fallback | GAP |
| JSON shape | Given `ps --json`; Then `{project, network, services[]{name,container,state,health,ports,image_id}}` is emitted | GAP |
| no command-specific flags | Given `ps --tail 10`; Then exit 1 | GAP (parser: `CommandLineTests.cs::RejectsUnknownOptionsPerCommand`) |

## logs

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| selected services | Given `logs api --tail 50 --timestamps -f`; When parsed; Then `tail`, `timestamps` and `follow` are captured for the command | `CommandLineTests.cs::SupportsEqualsSyntax`, `CommandLineTests.cs::FollowsContainersWithShortF`; forwarding to podman: GAP |
| missing container | Given a service with no container; When logs; Then exit 3 and suggests `narwhal up` | GAP |
| unknown service | Given `logs nope`; When logs; Then exit 1 lists known services | GAP |
| invalid tail | Given `--tail abc`; Then exit 1 | GAP (parser: `CommandLineTests.cs::ValidatesIntegerOptions`) |
| `--` terminator | Given `logs -- -weird-name`; Then the token is a positional, not a flag | `CommandLineTests.cs::TreatsEverythingAfterDoubleDashAsPositionals` |

## push

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| single platform | Given a lock for one selected foreign system; When push; Then that locked image id is pushed by id, not by tag | `MultiPlatformIntegrationTests.cs::PushingOneSelectedSystemPushesThatPlatformsImageId` |
| multi-arch index | Given two systems; When push; Then an empty index is created under a dedicated name, both ids added, pushed `--all --rm`, then removed | `MultiPlatformIntegrationTests.cs::BuildsVerifiesPlansPushesAndRunsEveryConfiguredSystem` |
| directory store | Given the locked store path is a directory; When push; Then the contained archive is read and pushed | `MultiPlatformIntegrationTests.cs::PushesWhenTheLockedStorePathIsADirectory` |
| host tag restoration | Given either system order; When push; Then the local tag ends on the host image | `MultiPlatformIntegrationTests.cs::PushLeavesTheTagOnTheHostForEitherSystemOrder` |
| stale host attr | Given a host entry whose attr no longer matches; When foreign push; Then the previous tag is restored with a warning | `MultiPlatformIntegrationTests.cs::StaleHostAttrFallsBackToThePreviousTag` |
| host reload mismatch | Given the host reload returns a different id; When foreign push; Then the previous tag is restored with a warning | `MultiPlatformIntegrationTests.cs::HostLoadMismatchWarnsAndFallsBackToThePreviousTag` |
| drifted image id | Given the lock image id differs from the archive; When push; Then exit 3 before any push | `MultiPlatformIntegrationTests.cs::DriftedLockImageIdBlocksPushBeforeAnyPush` |
| stale attr | Given the lock attr differs from the manifest; When push; Then exit 3 before any push | `MultiPlatformIntegrationTests.cs::StaleLockAttrBlocksPush` |
| archive collected | Given the archive is gone but the locked image is loaded; When push; Then the locked id is pushed | `MultiPlatformIntegrationTests.cs::PushesLoadedImageWhenTheArchiveWasCollected` |
| multi-arch archived fallback | Given both archives are gone but both ids are loaded; When push; Then both ids make the index | `MultiPlatformIntegrationTests.cs::MultiPlatformPushFallsBackToLoadedLockedIds` |
| stale index cleared | Given a leftover index with a stale platform; When push; Then the index is rebuilt from empty | `MultiPlatformIntegrationTests.cs::PushClearsStaleManifestEntries` |
| restore failure | Given push and tag restore both fail; When push; Then exit 3 and the original push error survives | `MultiPlatformIntegrationTests.cs::RestoreFailureDoesNotMaskTheOriginalPushFailure` |
| prebuilt refusal | Given `push db` where `db` is prebuilt; When push; Then exit 1 | GAP |
| missing lock entry | Given no lock entry for a selected service; When push; Then exit 3 suggests `narwhal build` | GAP |

## verify

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| all match | Given a matching lock; When `verify --no-load`; Then exit 0 and "every service reproduced" | `MultiPlatformIntegrationTests.cs::BuildsVerifiesPlansPushesAndRunsEveryConfiguredSystem` |
| one platform drifts | Given one drifted nar hash; When verify; Then exit 4 lists only that service/system | `MultiPlatformIntegrationTests.cs::VerifyReportsDriftForOnePlatform` |
| missing platform | Given a locked service missing one configured system; When verify; Then that pair is reported `missing` and exit is 4 | GAP |
| missing lock | Given no lock entries; When verify; Then exit 4 suggests `narwhal build` | GAP |
| build failure during verify | Given nix fails; When verify; Then the pair is listed `build failed` and exit is 4 | GAP |
| manifest changed | Given the manifest hash differs; When verify; Then a warning says attrs are verified against the old lock | GAP |
| JSON shape | Given `verify --json`; Then `{project, ok, services[]{name,system,platform,ok,detail}}` is emitted | GAP |

## version / help

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| version | Given `narwhal version`; When run; Then exit 0 and the version string is printed | GAP |
| help flags | Given `-h`, `--help`, or `help`; Then usage prints and exit is 0 without a manifest | GAP (parser: `CommandLineTests.cs::HelpDoesNotRequireACommand`) |
| command help | Given `narwhal help build`; Then that command's usage prints and exit is 0 | GAP |
| unknown command in help | Given `narwhal help nope`; Then top-level usage prints and exit is 0 | GAP |

## Global CLI and manifest failure paths

| Scenario | Given / When / Then | Test |
| --- | --- | --- |
| unknown command | Given `narwhal frobnicate`; Then exit 1 with usage | GAP (parser: `CommandLineTests.cs::RejectsUnknownCommands`) |
| unknown flag | Given a flag the command does not define; Then exit 1 with that command's usage | GAP (parser: `CommandLineTests.cs::RejectsUnknownOptionsPerCommand`, `CommandLineTests.cs::RejectsSystemOnCommandsWithoutIt`) |
| missing option value | Given `logs --tail`; Then exit 1 | GAP (parser: `CommandLineTests.cs::RejectsMissingOptionValues`) |
| unknown service | Given `build nope`; Then exit 1 lists known services | GAP |
| no command | Given no arguments; Then exit 1 "no command given" | GAP |
| upward discovery | Given a manifest in a parent and a nested cwd; When discovered; Then the parent manifest wins | `ManifestTests.cs::DiscoversManifestUpwards` |
| missing manifest | Given no `narwhal.toml` in cwd or parents; When plan; Then exit 1 suggests `narwhal init` | GAP |
| custom --file | Given `--file custom.toml`; Then that path is loaded relative to `-C` | `CommandLineTests.cs::UsesLongFileFlagWithoutStealingShortF` (parse only), loading: GAP |
| custom --lock | Given `--lock other.json`; Then that lock path is used | GAP |
| missing -C directory | Given `-C does-not-exist`; Then exit 1 | GAP |
| invalid TOML | Given a syntax error; Then exit 1 with `path:line:column` and a caret snippet | `TomlInvalidInputTests.cs::RejectsInvalidDocumentsAtTheRightPosition` (parser), CLI exit: GAP |
| duplicate key | Given two assignments to one key; Then `TomlException` names the duplicate | `TomlTests.cs::RejectsDuplicateKeys`, `TomlInvalidInputTests.cs::RejectsInvalidDocumentsAtTheRightPosition` |
| unterminated string/array | Given an unclosed quote, array, or multiline string; Then the error carries line/column | `TomlInvalidInputTests.cs::RejectsInvalidDocumentsAtTheRightPosition` |
| malformed table | Given an unclosed or conflicting header; Then the error carries line/column | `TomlInvalidInputTests.cs::RejectsInvalidDocumentsAtTheRightPosition` |
| invalid unicode escape | Given a surrogate or above-U+10FFFF escape; Then `TomlException`, never a runtime exception | `TomlInvalidInputTests.cs::RejectsUnicodeEscapesOutsideTheScalarRange` |
| dependency cycle | Given `a -> b -> a`; Then load fails naming the cycle | `ManifestTests.cs::RejectsDependencyCycles` |
| unknown dependency | Given `depends_on = ["nope"]`; Then load fails | `ManifestTests.cs::RejectsUnknownDependencies` |
| duplicate service | Given two `[[service]]` with one name; Then load fails | `ManifestTests.cs::RejectsDuplicateServiceNames` |
| no services | Given a manifest without `[[service]]`; Then load fails | `ManifestTests.cs::RejectsManifestWithoutServices` |
| unknown service key | Given `prot = [...]`; Then load fails and lists known keys | `ManifestTests.cs::RejectsUnknownKeys` |
| invalid port | Given `8080:99999`; Then load fails | `ManifestTests.cs::RejectsInvalidPorts` |
| unsupported system | Given `mips-linux`; Then load fails with the supported list | `ManifestTests.cs::RejectsUnsupportedSystemsWithTheSupportedList` |
| duplicate system | Given the same system twice; Then load fails | `ManifestTests.cs::RejectsDuplicateSystems` |
| empty systems | Given `systems = []`; Then load fails | `ManifestTests.cs::RejectsEmptySystems` |
| legacy system key | Given `build.system`; Then load fails pointing at `systems` | `ManifestTests.cs::RejectsTheLegacySystemKey` |
| plain attr, many systems | Given a plain attr and two systems; Then `AttrPath` fails naming the service and `{system}` | `ManifestTests.cs::RejectsAPlainAttrWithMultipleSystems`, `AttrPathTests.cs::PlainAttrWithMultipleSystemsNamesTheServiceAndTheFix` |
| JSON error envelope | Given any failure with `--json`; Then stdout is `{ok:false,error,exit_code}` and stderr carries the human message | GAP |
| verbose | Given `--verbose`; Then tool commands are echoed and `Detail` lines print | GAP |

## Property, table and edge-case coverage

| Area | What is asserted | Test |
| --- | --- | --- |
| TOML generated documents | 100 seeded documents built from a model round-trip to that model | `PropertyTests.cs::GeneratedDocumentsParseToTheirModel` |
| TOML reparse | 100 seeded documents parse identically twice | `PropertyTests.cs::GeneratedValidDocumentsNeverThrowOnReparse` |
| TOML values | integers (hex/octal/binary/separators/signs), floats (exponents/separators), date-times, empty containers, quoted keys, valid 8-digit `\U` escapes including the maximum scalar | `TomlInvalidInputTests.cs::ParsesEveryIntegerForm`, `ParsesFloatsWithExponentsAndSeparators`, `ParsesDateTimesAsRawTokens`, `ParsesCommentOnlyAndEmptyDocuments`, `ParsesEmptyContainersAndQuotedKeys`; `TomlTests.cs::ParsesEightDigitUnicodeEscapes`, `AcceptsTheMaximumUnicodeScalarAsASurrogatePair` |
| Lock round-trip | 30 seeded arbitrary service/platform sets survive save/load and save/load/save byte-identically | `LockFilePropertyTests.cs::ArbitraryPlatformSetsRoundTripDeterministically` |
| Lock empty | an empty lock round-trips without inventing entries | `LockFilePropertyTests.cs::AnEmptyLockRoundTripsWithoutInventingEntries` |
| Lock v1 migration | every supported system migrates from its attr, sets `NeedsMigration`, and clears it on save | `LockFilePropertyTests.cs::MigratesVersionOneForEverySupportedSystem`, `VersionTwoDoesNotNeedMigration`, `LockFileTests.cs::MigratesVersionOneAttrsWithoutASystemToTheHost`, `RejectsUnknownVersions`, `MissingLockStartsEmptyAtTheCurrentVersion` |
| Lock coverage matrix | full / missing service / missing platform / wrong attr / empty nar hash / wrong OCI platform / empty image id / empty lock / prebuilt absent / extra platform / empty store path | `LockFilePropertyTests.cs::CoverageMatrixMatchesTheLockedEvidence`, `CoverageUsesTheSubstitutedAttrForExplicitSystemAttrs`, `LockFileTests.cs::CoverageRequiresPlatformAndImageIdForEverySystem` |
| AttrPath | default attr, custom prefix, `{system}` substitution (including repeated placeholders), plain attr for one system, error for many | `AttrPathTests.cs::DefaultAttrIsPrefixSystemName`, `DefaultAttrHonoursACustomPrefix`, `SubstitutesTheSystemPlaceholder`, `SubstitutesEveryPlaceholderOccurrence`, `SubstitutesEvenWhenOnlyOneSystemIsConfigured`, `PlainAttrIsAllowedForASingleSystem`, `PlainAttrWithMultipleSystemsNamesTheServiceAndTheFix`, `TheHostSystemIsNotSpecialWhenThePlaceholderIsMissing`; `ManifestTests.cs::SubstitutesSystemInAnExplicitAttr`, `AllowsAPlainAttrForOneSystem`, `DefaultsImageAndAttributeFromProjectName` |
| ArchiveImage | `.tar`, `.tar.gz`, gzip magic, leading `./`, earlier entries, directory/Windows config paths, uppercase hex, empty manifest, missing/non-string Config, bad length/non-hex Config | `ArchiveImageTests.cs::ReadsTheImageIdFromADockerArchive`, `RejectsAnArchiveWithoutAManifest`, `RejectsMalformedManifestJsonWithABuildError`, `RejectsANonObjectManifestEntry`; `ArchiveImageEdgeCaseTests.cs::ReadsAManifestEntryWithALeadingDotSlash`, `DetectsGzipByMagicEvenWithoutTheGzExtension`, `SkipsEarlierEntriesUntilTheManifest`, `ReadsAConfigPathThatCarriesDirectories`, `ReadsAWindowsStyleConfigPath`, `LowercasesAnUppercaseConfigName`, `RejectsAnEmptyManifestArray`, `RejectsAnEntryWithoutConfig`, `RejectsAConfigThatIsNotAString`, `RejectsAConfigNameThatIsNotA64CharHexId` |

## GAP list

Command-level gaps (no `App.Main` acceptance test):

1. `init`: all scenarios (scaffold, name, overwrite guard, `--force`, `--narwhal-ref`, sanitisation).
2. `plan`: human table, every status value, prebuilt rows, stale warning, no-lock note, `--refresh`.
3. `build`: failing nix build (exit 2), `--locked` success, `--no-load` assertion, `--rebuild`, prebuilt filtering, no buildable services, atomic lock write.
4. `up`/`deploy`: dependency closure, network/volume creation, health waiting, prebuilt pull, host-lock reuse, `--no-build`, `--build`+`--no-build`, `--wait` bounds, `--json`.
5. `down`: all scenarios.
6. `ps`: human and JSON output.
7. `logs`: missing container, unknown service, podman argument forwarding.
8. `push`: prebuilt refusal, missing lock entry.
9. `verify`: missing lock, missing platform row, build failure row, manifest-changed warning, `--json`.
10. `version` and `help <command>` output.
11. Global: missing manifest, invalid TOML through the CLI, unknown service, no command, `--file`, `--lock`, missing `-C`, `--json` error envelope, `--verbose`.
12. Exit codes with no end-to-end test: 2 (build failure), 130 (interrupt), 3 for missing tool/container.
13. JSON shapes not asserted: `build`, `verify`, `up`, `down`, `ps`, error envelope; `version --json` is plain text by design.

Out of scope this round: fake-harness extraction, local-registry e2e, remaining push edge cases, cloud work.
