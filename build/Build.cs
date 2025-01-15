using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static System.Runtime.InteropServices.RuntimeInformation;
using static Nuke.Common.Tools.Git.GitTasks;
using static Nuke.Common.Tools.NuGet.NuGetTasks;

class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.BuildRuntimePackage, x => x.BuildMultiplatformPackage);

    [Parameter("Specify which package to install.")]
    readonly string VcpkgPackageName = "ffmpeg";

    [Parameter("Specify which version of the specified package to install.")]
    readonly string VcpkgPackageVersion = "7.1#1";

    [Parameter("Specify which baseline to use.")]
    readonly string VcpkgBaseline = "cf035d9916a0a23042b41fcae7ee0386d245af08";

    [Parameter("Specify whether the default features of the specified package should be installed or not.")]
    readonly bool VcpkgDefaultFeatures = true;

    [Parameter("Specify which optional features of the specified package to install.")]
    readonly string[] VcpkgFeatures = [];

    [Parameter("Specify the target architecture triplet(s).", Separator = ",")]
    readonly string[] VcpkgTriplets = [ "linux-x64", "win-x64", "win-x86" ];

    [Parameter("Specify the source(s) to use for binary caching.", Separator = ";")]
    readonly string[] VcpkgBinarySources = [];

    [Parameter("Specify the path(s) to containing overlay ports.", Separator = ";")]
    readonly AbsolutePath[] VcpkgOverlayPorts = [];

    [Parameter("Specify the path(s) to containing overlay triplets.", Separator = ";")]
    readonly AbsolutePath[] VcpkgOverlayTriplets =
    [
        RootDirectory / "vcpkg-triplets"
    ];

    [Parameter("Specify the NuGet package identifier.")]
    readonly string NuGetPackageID = "FFmpeg";

    [Parameter("Specifiy the NuGet package authors.")]
    readonly string[] NuGetAuthors = ["Ronald van Manen"];

    [Parameter("Specify the NuGet package's home page.")]
    readonly string NuGetProjectUrl = "https://github.com/ronaldvanmanen/FFmpeg-packaging";

    [Parameter("Specify the NuGet package's license.")]
    readonly string NuGetLicense = "LGPL-2.1-or-later";

    [Parameter("Specify the NuGet package's reposity URL.")]
    readonly string NuGetRepositoryUrl = "https://github.com/ronaldvanmanen/FFmpeg-packaging";

    [Parameter("The Azure DevOps personal access token")]
    [Secret]
    readonly string AzureToken;

    [Parameter("The GitHub personal access token")]
    [Secret]
    readonly string GitHubToken;

    static readonly IEnumerable<string> BuildDependenciesForLinux =
    [
        "autoconf",
        "autoconf-archive",
        "automake",
        "bison",
        "build-essential",
        "curl",
        "gcc-mingw-w64-x86-64",
        "g++-mingw-w64-x86-64",
        "libegl1-mesa-dev",
        "libgl-dev",
        "libglfw3-dev",
        "libtool",
        "libwayland-dev",
        "libxkbcommon-dev",
        "libx11-dev",
        "libxi-dev",
        "libxext-dev",
        "libxtst-dev",
        "mesa-common-dev",
        "meson",
        "mingw-w64",
        "nasm",
        "python3",
        "python3-pip",
        "python3-venv",
        "tar",
        "unzip",
        "yasm",
        "zip"
    ];

    IEnumerable<NuGetFeedSettings> NuGetFeeds
    {
        get
        {
            yield return new NuGetFeedSettings
            {
                Name = "nuget.org",
                Source = "https://api.nuget.org/v3/index.json",
            };
                
            yield return new NuGetFeedSettings
            {
                Name = "azure",
                Source = "https://pkgs.dev.azure.com/ronaldvanmanen/_packaging/ronaldvanmanen/nuget/v3/index.json",
                UserName = "GitHub",
                Password = AzureToken,
                StorePasswordInClearText = true,
                ApiKey = "AzureDevOps",
                Publish = true
            };

            yield return new NuGetFeedSettings
            {
                Name = "azure-vcpkg-binary-cache",
                Source = "https://pkgs.dev.azure.com/ronaldvanmanen/_packaging/vcpkg-binary-cache/nuget/v3/index.json",
                UserName = "GitHub",
                Password = AzureToken,
                StorePasswordInClearText = true,
                ApiKey = "AzureDevOps",
            };

            yield return new NuGetFeedSettings
            {
                Name = "github",
                Source = "https://nuget.pkg.github.com/ronaldvanmanen/index.json",
                UserName = "GitHub",
                Password = GitHubToken,
                StorePasswordInClearText = true,
                ApiKey = GitHubToken,
                Publish = true
            };
        }
    }

    AbsolutePath ArtifactsRootDirectory => RootDirectory / "artifacts";

    AbsolutePath VcpkgArtifactsRootDirectory => ArtifactsRootDirectory / "vcpkg";

    AbsolutePath NuGetArtifactsRootDirectory => ArtifactsRootDirectory / "nuget";

    AbsolutePath NuGetBuildRootDirectory => NuGetArtifactsRootDirectory / "build";

    AbsolutePath NuGetInstallRootDirectory => NuGetArtifactsRootDirectory / "installed";

    AbsolutePath VcpkgJsonFile => RootDirectory / "vcpkg.json";

    AbsolutePath VcpkgConfigurationJsonFile => RootDirectory / "vcpkg-configuration.json";

    AbsolutePath NuGetConfigFile => RootDirectory / "NuGet.config";

    [LocalPath(windowsPath: "vcpkg/bootstrap-vcpkg.bat", unixPath: "vcpkg/bootstrap-vcpkg.sh")]
    readonly Tool BootstrapVcpkg;

    [LocalPath(windowsPath: "vcpkg/vcpkg.exe", unixPath: "vcpkg/vcpkg")]
    readonly Tool Vcpkg;

    Tool Sudo => ToolResolver.GetPathTool("sudo");

    string GetDotNetRuntimeID(string vcpkgTriplet) =>
        vcpkgTriplet switch
        {
            "linux-x64" => "linux-x64",
            "win-x64" => "win-x64",
            "win-x86" => "win-x86",
            _ => throw new NotSupportedException($"The vcpkg triplet `{vcpkgTriplet} is not yet supported.")
        };

    string GetNuGetMultiplatformPackageID() =>
        $"{NuGetPackageID}";

    string GetNuGetRuntimePackageID(string vcpkgTriplet) =>
        $"{NuGetPackageID}.runtime.{GetDotNetRuntimeID(vcpkgTriplet)}";

    string GetNuGetPackageVersion(string vcpkgPackageVersion)
        => vcpkgPackageVersion.Replace('#', '.');

    AbsolutePath GetVcpkgBuildRootDirectory(string vcpkgTriplet) =>
        VcpkgArtifactsRootDirectory / $"{VcpkgPackageName}-{VcpkgPackageVersion.Replace('#', '.')}" / GetDotNetRuntimeID(vcpkgTriplet);

    AbsolutePath GetVcpkgBuildtreesRootDirectory(string vcpkgTriplet) =>
        GetVcpkgBuildRootDirectory(vcpkgTriplet) / "buildtrees";

    AbsolutePath GetVcpkgDownloadsRootDirectory(string vcpkgTriplet) =>
        GetVcpkgBuildRootDirectory(vcpkgTriplet) / "downloads";

    AbsolutePath GetVcpkgInstallRootDirectory(string vcpkgTriplet) =>
        GetVcpkgBuildRootDirectory(vcpkgTriplet) / "installed";

    AbsolutePath GetVcpkgPackagesRootDirectory(string vcpkgTriplet) =>
        GetVcpkgBuildRootDirectory(vcpkgTriplet) / "packages";

    AbsolutePath GetVcpkgInstallDirectory(string vcpkgTriplet) =>
        GetVcpkgInstallRootDirectory(vcpkgTriplet) / vcpkgTriplet;

    [UsedImplicitly]
    Target Clean => _ => _
        .Executes(() =>
        {
            ArtifactsRootDirectory.CreateOrCleanDirectory();
        });

    Target SetupVcpkg => _ => _
        .Requires(() => VcpkgPackageName)
        .Requires(() => VcpkgPackageVersion)
        .Requires(() => VcpkgDefaultFeatures)
        .Requires(() => VcpkgFeatures)
        .Executes(() =>
        {
            BootstrapVcpkg("-disableMetrics");

            VcpkgJsonFile.WriteJson(
                new JObject {
                    ["$schema"] = "https://raw.githubusercontent.com/microsoft/vcpkg-tool/main/docs/vcpkg.schema.json",
                    ["name"] = "ffmpeg-packaging",
                    ["dependencies"] = new JArray {
                        new JObject
                        {
                            ["name"] = VcpkgPackageName,
                            ["default-features"] = VcpkgDefaultFeatures,
                            ["features"] = new JArray(VcpkgFeatures)
                        }
                    },
                    ["overrides"] = new JArray
                    {
                        new JObject
                        {
                            ["name"] = VcpkgPackageName,
                            ["version"] = VcpkgPackageVersion
                        }
                    },
                    ["builtin-baseline"] = VcpkgBaseline
                }
            );

            VcpkgConfigurationJsonFile.WriteJson(
                new JObject
                {
                    ["$schema"] = "https://raw.githubusercontent.com/microsoft/vcpkg-tool/main/docs/vcpkg-configuration.schema.json",
                    ["overlay-ports"] = new JArray(VcpkgOverlayPorts.Select(e => e.ToString()).ToArray()),
                    ["overlay-triplets"] = new JArray(VcpkgOverlayTriplets.Select(e => e.ToString()).ToArray())
                }
            );
        });

    Target SetupNuGet => _ => _
        .Executes(() =>
        {
            NuGetConfigFile.DeleteFile();

            NuGetConfigFile.WriteXml(
                new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("configuration",
                        new XElement("packageSources",
                            new XElement("clear")
                        )
                    )
                )
            );

            foreach (var nugetFeed in NuGetFeeds)
            {
                var nuGetSourcesAddSettings = new NuGetSourcesAddSettings()
                    .SetConfigFile(NuGetConfigFile)
                    .SetName(nugetFeed.Name)
                    .SetSource(nugetFeed.Source)
                    .SetNonInteractive(IsServerBuild);

                if (!string.IsNullOrEmpty(nugetFeed.UserName) && !string.IsNullOrEmpty(nugetFeed.Password))
                {
                    nuGetSourcesAddSettings.SetUserName(nugetFeed.UserName);
                    nuGetSourcesAddSettings.SetPassword(nugetFeed.Password);
                }

                NuGetSourcesAdd(nuGetSourcesAddSettings);

                if (!string.IsNullOrWhiteSpace(nugetFeed.ApiKey))
                {
                    var argumentList = new List<string>
                    {
                        $"setApiKey {nugetFeed.ApiKey}",
                        $"-ConfigFile {NuGetConfigFile}",
                        $"-Source {nugetFeed.Source}"
                    };

                    if (IsServerBuild)
                    {
                        argumentList.Add("-NonInteractive");
                    }

                    ArgumentStringHandler arguments = "";
                    arguments.AppendLiteral(string.Join(' ', argumentList));
                    NuGetTasks.NuGet(arguments);
                }
            }
        });
    
    Target SetupBuildDependencies => _ => _
        .Unlisted()
        .Executes(() => 
        {
            if (IsOSPlatform(OSPlatform.Linux))
            {
                Sudo($"apt-get update");
                var dependencies = string.Join(' ', BuildDependenciesForLinux);
                Sudo($"apt-get -y install {dependencies:nq}");
            }
        });

    Target BuildPortPackage => _ => _
        .After(Clean)
        .DependsOn(SetupVcpkg)
        .DependsOn(SetupNuGet)
        .DependsOn(SetupBuildDependencies)
        .Requires(() => VcpkgFeatures)
        .Requires(() => VcpkgTriplets)
        .Executes(() => VcpkgTriplets.ForEach(vcpkgTriplet =>
        {
            var vcpkgBuildtreesRootDirectory = GetVcpkgBuildtreesRootDirectory(vcpkgTriplet);
            var vcpkgDownloadsRootDirectory = GetVcpkgDownloadsRootDirectory(vcpkgTriplet);
            var vcpkgInstallRootDirectory = GetVcpkgInstallRootDirectory(vcpkgTriplet);
            var vcpkgPackagesRootDirectory = GetVcpkgPackagesRootDirectory(vcpkgTriplet);

            vcpkgBuildtreesRootDirectory.CreateDirectory();
            vcpkgDownloadsRootDirectory.CreateDirectory();
            vcpkgInstallRootDirectory.CreateDirectory();
            vcpkgPackagesRootDirectory.CreateDirectory();

            var argumentList = new List<string>
            {
                $"install",
                $"--triplet={vcpkgTriplet}",
                $"--downloads-root={vcpkgDownloadsRootDirectory}",
                $"--x-buildtrees-root={vcpkgBuildtreesRootDirectory}",
                $"--x-install-root={vcpkgInstallRootDirectory}",
                $"--x-packages-root={vcpkgPackagesRootDirectory}",
                //$"--clean-after-build",
                $"--disable-metrics",
            };

            argumentList.AddRange(
                VcpkgBinarySources.Select(vcpkgBinarySource => $"--binarysource={vcpkgBinarySource}")
            );

            if (Logging.Level == LogLevel.Trace)
            {
                argumentList.Add("--debug");
            }

            // NOTE: ArgumentStringHandler assumes our arguments must be double qouted because we
            // pass our arguments as a single string. To prevent this from happening we explicity
            // create an ArgumentStringHandler and append our arguments as a string literal.
            ArgumentStringHandler arguments = "";
            arguments.AppendLiteral(string.Join(' ', argumentList));
            Vcpkg(arguments);
        }));

    Target BuildRuntimePackage => _ => _
        .After(Clean)
        .DependsOn(BuildPortPackage)
        .Requires(() => VcpkgPackageVersion)
        .Requires(() => VcpkgFeatures)
        .Requires(() => VcpkgTriplets)
        .Executes(() => VcpkgTriplets.ForEach(vcpkgTriplet =>
        {
            var dotNetRuntimeID = GetDotNetRuntimeID(vcpkgTriplet);
            var packageID = GetNuGetRuntimePackageID(vcpkgTriplet);
            var packageVersion = GetNuGetPackageVersion(VcpkgPackageVersion);
            var packageBuildDirectory = NuGetBuildRootDirectory / $"{packageID}.{packageVersion}.nupkg";
            var packageSpecFile = packageBuildDirectory / $"{packageID}.nuspec";

            packageBuildDirectory.CreateOrCleanDirectory();

            packageSpecFile.WriteXml(
                new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}package",
                        new XElement("metadata",
                            new XAttribute("minClientVersion", "2.12"),
                            new XElement("id", packageID),
                            new XElement("version", packageVersion),
                            new XElement("authors", NuGetAuthors),
                            new XElement("requireLicenseAcceptance", true),
                            new XElement("license", new XAttribute("type", "expression"), NuGetLicense),
                            new XElement("projectUrl", NuGetProjectUrl),
                            new XElement("description", $"{dotNetRuntimeID} runtime library for {NuGetPackageID}."),
                            new XElement("copyright", $"Copyright © {NuGetAuthors}"),
                            new XElement("repository",
                                new XAttribute("type", "git"),
                                new XAttribute("url", NuGetRepositoryUrl)
                            )
                        )
                    )
                )
            );

            var libraryTargetDirectory = packageBuildDirectory / "runtimes" / $"{dotNetRuntimeID}" / "native";
            var vcpkgInstallRootDirectory = GetVcpkgInstallDirectory(vcpkgTriplet);
            var libraryFiles = vcpkgInstallRootDirectory.GlobFiles("lib/*.so*", $"bin/*.dll");
            foreach (var libraryFile in libraryFiles)
            {
                libraryFile.CopyToDirectory(libraryTargetDirectory);
            }

            var targetPath = packageBuildDirectory.GetRelativePathTo(packageSpecFile);
            var packSettings = new NuGetPackSettings()
                .SetProcessWorkingDirectory(packageBuildDirectory)
                .SetTargetPath(targetPath)
                .SetOutputDirectory(NuGetInstallRootDirectory);

            NuGetPack(packSettings);
        }));

    Target BuildMultiplatformPackage => _ => _
        .After(Clean)
        .DependsOn(BuildPortPackage)
        .Requires(() => VcpkgFeatures)
        .Requires(() => VcpkgTriplets)
        .Executes(() =>
        {
            var packageID = GetNuGetMultiplatformPackageID();
            var packageVersion = GetNuGetPackageVersion(VcpkgPackageVersion);
            var packageBuildDirectory = NuGetBuildRootDirectory / $"{packageID}.{packageVersion}.nupkg";
            var packageSpecFile = packageBuildDirectory / $"{packageID}.nuspec";
            var runtimeJsonFile = packageBuildDirectory / "runtime.json";
            var placeholderFiles = new []
            {
                packageBuildDirectory / "lib" / "netstandard2.0" / "_._"
            };

            packageBuildDirectory.CreateOrCleanDirectory();

            packageSpecFile.WriteXml(
                new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}package",
                        new XElement("metadata",
                            new XAttribute("minClientVersion", "2.12"),
                            new XElement("id", packageID),
                            new XElement("version", packageVersion),
                            new XElement("authors", NuGetAuthors),
                            new XElement("requireLicenseAcceptance", true),
                            new XElement("license", new XAttribute("type", "expression"), NuGetLicense),
                            new XElement("projectUrl", NuGetProjectUrl),
                            new XElement("description", $"Multi-platform native library for {NuGetPackageID}."),
                            new XElement("copyright", $"Copyright © {NuGetAuthors}"),
                            new XElement("repository",
                                new XAttribute("type", "git"),
                                new XAttribute("url", NuGetRepositoryUrl)
                            ),
                            new XElement("dependencies",
                                new XElement("group",
                                    new XAttribute("targetFramework", ".NETStandard2.0")
                                )
                            )
                        )
                    )
                )
            );

            var includeTargetDirectory = packageBuildDirectory / "lib" / "native" / "include";
            var includeDirectories = VcpkgTriplets.Select(vcpkgTriplet => GetVcpkgInstallDirectory(vcpkgTriplet) / "include");
            foreach (var includeDirectory in includeDirectories)
            {
                includeDirectory.Copy(includeTargetDirectory, ExistsPolicy.MergeAndSkip);
            }

            runtimeJsonFile.WriteJson(
                new JObject
                {
                    ["runtimes"] = new JObject(
                        VcpkgTriplets.Select(vcpkgTriplet =>
                        {
                            var runtimeID = GetDotNetRuntimeID(vcpkgTriplet);
                            var runtimePackageID = GetNuGetRuntimePackageID(vcpkgTriplet);
                            var runtimePackageVersion = GetNuGetPackageVersion(VcpkgPackageVersion);
                            return new JProperty(runtimeID, new JObject(
                                new JProperty(packageID, new JObject(
                                    new JProperty(runtimePackageID, $"[{runtimePackageVersion}]")
                                )
                            )));
                        })
                    )
                });

            foreach (var placeholder in placeholderFiles)
            {
                placeholder.TouchFile();
            }

            var targetPath = packageBuildDirectory.GetRelativePathTo(packageSpecFile);
            var packSettings = new NuGetPackSettings()
                .SetProcessWorkingDirectory(packageBuildDirectory)
                .SetTargetPath(targetPath)
                .SetOutputDirectory(NuGetInstallRootDirectory)
                .SetNoPackageAnalysis(true);

            NuGetPack(packSettings);
        });

    [UsedImplicitly]
    Target PublishPackage => _ => _
        .After(BuildRuntimePackage)
        .After(BuildMultiplatformPackage)
        .Executes(() => 
        {
            var packages = NuGetInstallRootDirectory.GlobFiles("*.nupkg");
            foreach (var nugetFeed in NuGetFeeds.Where(e => e.Publish))
            {
                foreach (var package in packages)
                {
                    NuGetPush(
                        new NuGetPushSettings()
                            .SetTargetPath(package)
                            .SetSource(nugetFeed.Source)
                            .SetApiKey(nugetFeed.ApiKey)
                            .SetNonInteractive(IsServerBuild)
                    );
                }
            }
        });        
}
