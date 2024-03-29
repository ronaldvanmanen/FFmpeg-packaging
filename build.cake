//////////////////////////////////////////////////////////////////////
// ADD-INS
//////////////////////////////////////////////////////////////////////

#addin nuget:?package=Cake.FileHelpers&version=6.1.3
#addin nuget:?package=Cake.Git&version=3.0.0
#addin nuget:?package=NuGet.Packaging&Version=6.6.1&loaddependencies=true

//////////////////////////////////////////////////////////////////////
// TOOLS
//////////////////////////////////////////////////////////////////////

#tool dotnet:?package=GitVersion.Tool&version=5.10.3

//////////////////////////////////////////////////////////////////////
// USINGS
//////////////////////////////////////////////////////////////////////

using System.Text.RegularExpressions;
using Cake.Common.IO.Paths;
using NuGet.RuntimeModel;
using NuGet.Versioning;

//////////////////////////////////////////////////////////////////////
// VARIABLES
//////////////////////////////////////////////////////////////////////

var repoRoot = GitFindRootFromPath(Context.Environment.WorkingDirectory);

var vcpkgRoot = repoRoot + Directory("vcpkg");

var artifactsRoot = repoRoot + Directory("artifacts");
var vcpkgArtifactsRoot = artifactsRoot + Directory("vcpkg");
var nugetArtifactsRoot = artifactsRoot + Directory("nuget");
var nugetBuildRoot = nugetArtifactsRoot + Directory("build");
var nugetInstallRoot = nugetArtifactsRoot + Directory("installed");

internal sealed class NuGetSourceInfo
{
    public string Name { get; set; }
    public string Source { get; set; }
    public bool IsEnabled { get; set; }
}

var nugetSources = new Dictionary<string, string>
{
    { 
        "nuget.org",
        "https://api.nuget.org/v3/index.json"
    },
    {
        "azure",
        "https://pkgs.dev.azure.com/ronaldvanmanen/_packaging/ronaldvanmanen/nuget/v3/index.json"
    },
    {
        "azure-vcpkg-binary-cache",
        "https://pkgs.dev.azure.com/ronaldvanmanen/_packaging/vcpkg-binary-cache/nuget/v3/index.json"
    },
    {
        "github",
        "https://nuget.pkg.github.com/ronaldvanmanen/index.json"
    }
};

//////////////////////////////////////////////////////////////////////
// METHODS
//////////////////////////////////////////////////////////////////////

internal ConvertableDirectoryPath Directory(string basePath, string childPath, params string[] additionalChildPaths)
{
    var path = Directory(basePath) + Directory(childPath);
    foreach (var additionalChildPath in additionalChildPaths)
    {
        path += Directory(additionalChildPath);
    }
    return path;
}

internal void EnsureDirectoriesExists(params ConvertableDirectoryPath[] paths)
{
    foreach (var path in paths)
    {
        EnsureDirectoryExists(path);
    }
}

internal ConvertableFilePath FindVcpkgBootstrapScript(ConvertableDirectoryPath searchPath)
{
    if (IsRunningOnLinux())
    {
        var scriptPath = searchPath + File("bootstrap-vcpkg.sh");
        if (!FileExists(scriptPath))
        {
            throw new Exception("Could not find `bootstrap-vcpkg.sh`");
        }
        return scriptPath;
    }

    if (IsRunningOnWindows())
    {
        var scriptPath = searchPath + File("bootstrap-vcpkg.bat");
        if (!FileExists(scriptPath))
        {
            throw new Exception("Could not find `bootstrap-vcpkg.bat`");
        }
        return scriptPath;
    }

    throw new PlatformNotSupportedException();
}

internal ConvertableFilePath FindVcpkgExecutable(ConvertableDirectoryPath searchPath)
{
    if (IsRunningOnLinux())
    {
        var scriptPath = searchPath + File("vcpkg");
        if (!FileExists(scriptPath))
        {
            throw new Exception("Could not find `vcpkg`");
        }
        return scriptPath;
    }

    if (IsRunningOnWindows())
    {
        var scriptPath = searchPath + File("vcpkg.exe");
        if (!FileExists(scriptPath))
        {
            throw new Exception("Could not find `vcpkg.exe`");
        }
        return scriptPath;
    }

    throw new PlatformNotSupportedException();
}

internal string DotNetRuntimeIdentifier(string vcpkgTriplet)
{
    if (vcpkgTriplet == "x64-linux-dynamic-release")
    {
        return "linux-x64";
    }

    if (vcpkgTriplet == "x64-windows-release")
    {
        return "win-x64";
    }
    
    if (vcpkgTriplet == "x86-windows-release")
    {
        return "win-x86";
    }

    throw new NotSupportedException($"The vcpkg triplet `{vcpkgTriplet} is not yet supported.");
}

internal string NuGetRuntimePackageName(string vcpkgFeature, string vcpkgTriplet)
{
    var dotnetRuntimeIdentifier = DotNetRuntimeIdentifier(vcpkgTriplet);
    if (vcpkgFeature == "no-deps")
    {
        return $"FFmpeg.runtime.{dotnetRuntimeIdentifier}";
    }
    else
    {
        return $"FFmpeg.{vcpkgFeature}.runtime.{dotnetRuntimeIdentifier}";
    }
}

internal string NuGetMultiplatformPackageName(string vcpkgFeature)
{
    if (vcpkgFeature == "no-deps")
    {
        return $"FFmpeg";
    }
    else
    {
        return $"FFmpeg.{vcpkgFeature}";
    }
}

internal ConvertableDirectoryPath VcpkgBuildtreesRoot(string vcpkgFeature, string vcpkgTriplet)
{
    return vcpkgArtifactsRoot + Directory(vcpkgFeature, vcpkgTriplet, "buildtrees");

}

internal ConvertableDirectoryPath VcpkgDownloadsRoot(string vcpkgFeature, string vcpkgTriplet)
{
    return vcpkgArtifactsRoot + Directory(vcpkgFeature, vcpkgTriplet, "downloads");
}

internal ConvertableDirectoryPath VcpkgInstallRoot(string vcpkgFeature, string vcpkgTriplet)
{
    return vcpkgArtifactsRoot + Directory(vcpkgFeature, vcpkgTriplet, "installed");
}

internal ConvertableDirectoryPath VcpkgPackagesRoot(string vcpkgFeature, string vcpkgTriplet)
{
    return vcpkgArtifactsRoot + Directory(vcpkgFeature, vcpkgTriplet, "packages");
}

internal static ProcessArgumentBuilder AppendRange(this ProcessArgumentBuilder builder, IEnumerable<string> arguments)
{
    foreach (var argument in arguments)
    {
        builder.Append(argument);
    }
    return builder;
}

internal IEnumerable<NuGetSourceInfo> NuGetListSources(string configFile)
{
    var executable = Context.Tools.Resolve(new string[] { "nuget", "nuget.exe" });
    var arguments = new ProcessArgumentBuilder()
        .Append("sources")
        .Append("List")
        .Append("-Format Detailed");

    if (configFile is not null)
    {
        arguments.Append($"-ConfigFile {configFile}");
    }

    var exitCode = StartProcess(executable,
        new ProcessSettings
        {
            Arguments = arguments,
            RedirectStandardOutput = true
        },
        out var redirectedStandardOutput);

    if (exitCode != 0)
    {
        throw new Exception("Failed to execute `nuget sources list [...]`");
    }

    var outputLines = redirectedStandardOutput.ToList();
    for (var index = 1; index < outputLines.Count - 1; index += 2)
    {
        var match = Regex.Match(outputLines[index], "^\\s*\\d+\\.\\s+(?<SourceName>.+)\\s+\\[(?<SourceState>[^\\]]+)\\]");
        if (match.Success)
        {
            var name = match.Groups["SourceName"].Value;
            var state = match.Groups["SourceState"].Value;
            var source = outputLines[index + 1].Trim();
            yield return new NuGetSourceInfo
            {
                Name = name,
                Source = source,
                IsEnabled = state == "Enabled"
            };
        }
    }
}

internal void NuGetUpdateSource(string name, string source, NuGetSourcesSettings settings)
{
    var executable = Context.Tools.Resolve(new string[] { "nuget", "nuget.exe" });
    var arguments = new ProcessArgumentBuilder()
        .Append($"sources")
        .Append($"Update")
        .Append($"-Name {name}")
        .Append($"-Source {source}");

    if (settings.UserName is not null)
    {
        arguments.Append($"-Username {settings.UserName}");
    }

    if (settings.Password is not null)
    {
        arguments.Append($"-Password {settings.Password}");
    }

    if (settings.StorePasswordInClearText)
    {
        arguments.Append($"-StorePasswordInClearText");
    }
    

    if (settings.ConfigFile is not null)
    {
        arguments.Append($"-ConfigFile {settings.ConfigFile}");
    }

    var exitCode = StartProcess(executable, new ProcessSettings { Arguments = arguments });
    if (exitCode != 0)
    {
        throw new Exception("Failed to execute `nuget sources update [...]`");
    }
}

internal void NuGetAddOrUpdateSource(string name, string source, NuGetSourcesSettings settings)
{
    if (NuGetHasSource(source, settings))
    {
        NuGetUpdateSource(name, source, settings);
    }
    else
    {
        NuGetAddSource(name, source, settings);
    }
}

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean").Does(() => 
{
    CleanDirectory(artifactsRoot);
});

Task("Setup-Vcpkg").Does(() =>
{
    var vcpkgBootstrapScript = FindVcpkgBootstrapScript(vcpkgRoot);
    var exitCode = StartProcess(vcpkgBootstrapScript, "-disableMetrics");
    if (exitCode != 0)
    {
        throw new Exception("Failed to bootstrap `vcpkg`.");
    }
});

Task("Setup-NuGet-Source").Does(() =>
{
    var configFile = Argument<FilePath>("nuget-configfile", "NuGet.config");
    var sourceName = Argument<string>("nuget-source");
    var username = Argument<string>("nuget-username", null);
    var password = Argument<string>("nuget-password", null);
    var apikey = Argument<string>("apikey", null);
    
    if (!FileExists(configFile))
    {
        FileWriteText(configFile, "<?xml version=\"1.0\" encoding=\"utf-8\"?><configuration><packageSources><clear /></packageSources></configuration>");
    }

    NuGetAddOrUpdateSource(sourceName, nugetSources[sourceName], new NuGetSourcesSettings
    {
        ConfigFile = configFile,
        UserName = username,
        Password = password,
        StorePasswordInClearText = true
    });

    if (apikey is not null)
    {
        NuGetSetApiKey(apikey, nugetSources[sourceName], new NuGetSetApiKeySettings
        {
            ConfigFile = configFile
        });
    }
});

Task("Setup-Build-Dependencies").Does(() => 
{
    if (IsRunningOnLinux())
    {
        if (0 != StartProcess("sudo", "apt-get update"))
        {
            throw new Exception("Failed to execute `sudo apt-get update`.");
        }

        if (0 != StartProcess("sudo", "apt-get install nasm libgl-dev libglfw3-dev"))
        {
            throw new Exception("Failed to execute `sudo apt-get install [...]`.");
        }
    }
});

Task("Restore").DoesForEach(() => Arguments<string>("vcpkg-triplet"), vcpkgTriplet =>
{
    var vcpkgFeature = Argument<string>("vcpkg-feature");
    var vcpkgBinarySources = Arguments<string>("vcpkg-binarysource");
    var vcpkgDebug = HasEnvironmentVariable("RUNNER_DEBUG");

    var vcpkgBuildtreesRoot = VcpkgBuildtreesRoot(vcpkgFeature, vcpkgTriplet);
    var vcpkgDownloadsRoot = VcpkgDownloadsRoot(vcpkgFeature, vcpkgTriplet);
    var vcpkgInstallRoot = VcpkgInstallRoot(vcpkgFeature, vcpkgTriplet);
    var vcpkgPackagesRoot = VcpkgPackagesRoot(vcpkgFeature, vcpkgTriplet);

    EnsureDirectoriesExists(
        vcpkgBuildtreesRoot,
        vcpkgDownloadsRoot,
        vcpkgInstallRoot,
        vcpkgPackagesRoot);

    var vcpkgExecutable = FindVcpkgExecutable(vcpkgRoot);
    var vcpkgArguments = new ProcessArgumentBuilder()
        .Append($"install")
        .Append($"--only-binarycaching")
        .Append($"--triplet={vcpkgTriplet}")
        .Append($"--downloads-root={vcpkgDownloadsRoot}")
        .Append($"--x-buildtrees-root={vcpkgBuildtreesRoot}")
        .Append($"--x-install-root={vcpkgInstallRoot}")
        .Append($"--x-packages-root={vcpkgPackagesRoot}")
        .Append($"--x-no-default-features")
        .Append($"--x-feature={vcpkgFeature}")
        .Append($"--clean-after-build")
        .Append($"--disable-metrics")
        .AppendRange(vcpkgBinarySources.Select(vcpkgBinarySource => $"--binarysource={vcpkgBinarySource}"));

    if (vcpkgDebug)
    {
        vcpkgArguments.Append("--debug");
    }

    var exitCode = StartProcess(vcpkgExecutable, new ProcessSettings { Arguments = vcpkgArguments });
    if (exitCode != 0)
    {
        throw new Exception("Failed to restore packages.");
    }
});

Task("Build").DoesForEach(() => Arguments<string>("vcpkg-triplet"), vcpkgTriplet =>
{
    var vcpkgFeature = Argument<string>("vcpkg-feature");
    var vcpkgBinarySources = Arguments<string>("vcpkg-binarysource", Array.Empty<string>());
    var vcpkgDebug = HasEnvironmentVariable("RUNNER_DEBUG");

    var vcpkgBuildtreesRoot = VcpkgBuildtreesRoot(vcpkgFeature, vcpkgTriplet);
    var vcpkgDownloadsRoot = VcpkgDownloadsRoot(vcpkgFeature, vcpkgTriplet);
    var vcpkgInstallRoot = VcpkgInstallRoot(vcpkgFeature, vcpkgTriplet);
    var vcpkgPackagesRoot = VcpkgPackagesRoot(vcpkgFeature, vcpkgTriplet);

    EnsureDirectoriesExists(
        vcpkgBuildtreesRoot,
        vcpkgDownloadsRoot,
        vcpkgInstallRoot,
        vcpkgPackagesRoot);

    var vcpkgExecutable = FindVcpkgExecutable(vcpkgRoot);
    var vcpkgArguments = new ProcessArgumentBuilder()
        .Append($"install")
        .Append($"--triplet={vcpkgTriplet}")
        .Append($"--downloads-root={vcpkgDownloadsRoot}")
        .Append($"--x-buildtrees-root={vcpkgBuildtreesRoot}")
        .Append($"--x-install-root={vcpkgInstallRoot}")
        .Append($"--x-packages-root={vcpkgPackagesRoot}")
        .Append($"--x-no-default-features")
        .Append($"--x-feature={vcpkgFeature}")
        .Append($"--clean-after-build")
        .Append($"--disable-metrics")
        .AppendRange(vcpkgBinarySources.Select(vcpkgBinarySource => $"--binarysource={vcpkgBinarySource}"));

    if (vcpkgDebug)
    {
        vcpkgArguments.Append("--debug");
    }

    var exitCode = StartProcess(vcpkgExecutable, new ProcessSettings { Arguments = vcpkgArguments });
    if (exitCode != 0)
    {
        throw new Exception("Failed to build and install packages.");
    }
});

Task("Pack")
    .IsDependentOn("Pack-Multiplatform-Package")
    .IsDependentOn("Pack-Runtime-Package")
    .Does(() => {});

Task("Pack-Multiplatform-Package").Does(() =>
{
    EnsureDirectoriesExists(nugetBuildRoot, nugetInstallRoot);

    var vcpkgFeature = Argument<string>("vcpkg-feature");
    var vcpkgTriplets = Arguments<string>("vcpkg-triplet");
    
    var gitVersion = GitVersion();
    var nugetPackageVersion = gitVersion.NuGetVersion;
    var nugetPackageLicense = Argument<string>("nuget-license");
    var nugetPackageName = NuGetMultiplatformPackageName(vcpkgFeature);
    var nugetPackageDir = nugetBuildRoot + Directory(nugetPackageName);

    EnsureDirectoriesExists(nugetPackageDir);

    var nugetRuntimePackageVersion = new VersionRange(new NuGetVersion(nugetPackageVersion));
    var nugetRuntimeGraph = new RuntimeGraph(
        vcpkgTriplets.Select(vcpkgTriplet => {
            var dotnetRuntimeIdentifier = DotNetRuntimeIdentifier(vcpkgTriplet);
            var nugetRuntimePackageName = NuGetRuntimePackageName(vcpkgFeature, vcpkgTriplet);
            var nugetRuntimeDescription = new RuntimeDescription(dotnetRuntimeIdentifier, new []
            {
                new RuntimeDependencySet(nugetPackageName, new []
                {
                    new RuntimePackageDependency(nugetRuntimePackageName, nugetRuntimePackageVersion)
                })
            });
            return nugetRuntimeDescription;
        }));
    var nugetRuntimeFile = nugetPackageDir + File("runtime.json");

    JsonRuntimeFormat.WriteRuntimeGraph(nugetRuntimeFile, nugetRuntimeGraph);

    var placeholderFile = nugetPackageDir + File("_._");
    
    FileWriteText(placeholderFile, "");

    var nugetPackSettings = new NuGetPackSettings
    {
        Id = nugetPackageName,
        Version = nugetPackageVersion,
        Authors = new[] { "Ronald van Manen" },
        Owners = new[] { "Ronald van Manen" },
        RequireLicenseAcceptance = true,
        Description = "Multi-platform native library for FFmpeg.",
        License = new NuSpecLicense { Type = "expression", Value = nugetPackageLicense },
        ProjectUrl = new Uri("https://github.com/ronaldvanmanen/FFmpeg-packaging"),
        Copyright = "Copyright © Ronald van Manen",
        Repository = new NuGetRepository { Type="git", Url = "https://github.com/ronaldvanmanen/FFmpeg-packaging" },
        Dependencies = new []
        {
            new NuSpecDependency { TargetFramework = ".NETStandard2.0" }
        },
        BasePath = artifactsRoot,
        OutputDirectory = nugetInstallRoot
    };

    if (IsRunningOnWindows())
    {
        nugetPackSettings.Files = new List<NuSpecContent>
        {
            new NuSpecContent
            {
                Source = $"nuget\\build\\{nugetPackageName}\\Runtime.json",
                Target = "."
            },
            new NuSpecContent
            {
                Source = $"nuget\\build\\{nugetPackageName}\\_._",
                Target = "lib\\netstandard2.0"
            }
        };

        var vcpkgTriplet = vcpkgTriplets.First();

        nugetPackSettings.Files.Add(
            new NuSpecContent
            {
                Source = $"vcpkg\\{vcpkgFeature}\\{vcpkgTriplet}\\installed\\{vcpkgTriplet}\\include\\**\\*.h",
                Target = $"build\\native\\include"
            });
    }
    else
    {
        nugetPackSettings.Files = new List<NuSpecContent>
        {
            new NuSpecContent
            {
                Source = $"nuget/build/{nugetPackageName}/Runtime.json",
                Target = "."
            },
            new NuSpecContent
            {
                Source = $"nuget/build/{nugetPackageName}/_._",
                Target = "lib/netstandard2.0"
            }
        };

        var vcpkgTriplet = vcpkgTriplets.First();

        nugetPackSettings.Files.Add(
            new NuSpecContent
            {
                Source = $"vcpkg/{vcpkgFeature}/{vcpkgTriplet}/installed/{vcpkgTriplet}/include/**/*.h",
                Target = $"build/native/include"
            });
    }

    NuGetPack(nugetPackSettings);
});

Task("Pack-Runtime-Package").DoesForEach(() => Arguments<string>("vcpkg-triplet"), (vcpkgTriplet) => 
{
    EnsureDirectoriesExists(nugetArtifactsRoot, nugetInstallRoot);

    var vcpkgFeature = Argument<string>("vcpkg-feature");
    var vcpkgInstallRoot = VcpkgInstallRoot(vcpkgFeature, vcpkgTriplet);
    var gitVersion = GitVersion();
    var dotnetRuntimeIdentifier = DotNetRuntimeIdentifier(vcpkgTriplet);
    var nugetPackageLicense = Argument<string>("nuget-license");
    var nugetPackageName = NuGetRuntimePackageName(vcpkgFeature, vcpkgTriplet);
    var nugetPackBasePath = vcpkgInstallRoot + Directory(vcpkgTriplet);
    var nugetPackSettings = new NuGetPackSettings
    {
        Id = nugetPackageName,
        Version = gitVersion.NuGetVersion,
        Authors = new[] { "Ronald van Manen" },
        Owners = new[] { "Ronald van Manen" },
        RequireLicenseAcceptance = true,
        Description = $"{dotnetRuntimeIdentifier} native library for FFmpeg.",
        License = new NuSpecLicense { Type = "expression", Value = nugetPackageLicense },
        ProjectUrl = new Uri("https://github.com/ronaldvanmanen/FFmpeg-packaging"),
        Copyright = "Copyright © Ronald van Manen",
        Repository = new NuGetRepository { Type = "git", Url = "https://github.com/ronaldvanmanen/FFmpeg-packaging" },
        BasePath = nugetPackBasePath,
        OutputDirectory = nugetInstallRoot
    };

    if (IsRunningOnWindows())
    {
        if (vcpkgTriplet.Contains("windows"))
        {
            nugetPackSettings.Files = new []
            {
                new NuSpecContent { Source = "bin\\*.dll", Target = $"runtimes\\{dotnetRuntimeIdentifier}\\native"}
            };
        }
        else
        {
            nugetPackSettings.Files = new []
            {
                new NuSpecContent { Source = "lib\\*.so*", Target = $"runtimes\\{dotnetRuntimeIdentifier}\\native"}
            };
        }
    }
    else
    {
        if (vcpkgTriplet.Contains("windows"))
        {
            nugetPackSettings.Files = new []
            {
                new NuSpecContent { Source = "bin/*.dll", Target = $"runtimes/{dotnetRuntimeIdentifier}/native"}
            };
        }
        else
        {
            nugetPackSettings.Files = new []
            {
                new NuSpecContent { Source = "lib/*.so*", Target = $"runtimes/{dotnetRuntimeIdentifier}/native"},
            };
        }
    }

    NuGetPack(nugetPackSettings);
});

Task("Publish").Does(() =>
{
    var nugetSourceName = Argument<string>("nuget-source");
    var nugetApiKey = Argument<string>("nuget-apikey");
    var nugetPushSettings = new DotNetNuGetPushSettings
    {
        Source = nugetSources[nugetSourceName],
        ApiKey = nugetApiKey,
        SkipDuplicate = true,
    };

    var files = GetFiles($"{nugetInstallRoot}/**/*.nupkg");
    foreach(var file in files)
    {
        DotNetNuGetPush(file, nugetPushSettings);
    }
});

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(Argument<string>("target"));