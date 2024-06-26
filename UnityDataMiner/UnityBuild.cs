using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AssetRipper.Primitives;
using BepInEx.AssemblyPublicizer;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Serilog;

namespace UnityDataMiner
{
    public class UnityBuild
    {
        public string? Id { get; }

        public UnityVersion Version { get; }

        public string ShortVersion { get; }

        public NuGetVersion NuGetVersion { get; }

        public string UnityLibsZipFilePath { get; }

        public string MonoPath { get; }

        public string AndroidPath { get; }

        public string NuGetPackagePath { get; }

        public string InfoCacheDir { get; }

        public string CorlibZipPath { get; }

        public string LibIl2CppSourceZipPath { get; }

        public bool IsRunNeeded => !File.Exists(UnityLibsZipFilePath) || !File.Exists(NuGetPackagePath) ||
                                   !File.Exists(CorlibZipPath) || !Directory.Exists(MonoPath) ||
                                   (HasLibIl2Cpp && !File.Exists(LibIl2CppSourceZipPath)) ||
                                   (!Version.IsMonolithic() && !Directory.Exists(AndroidPath));

        public string BaseDownloadUrl => Version.GetDownloadUrl() + (Id == null ? string.Empty : $"{Id}/");

        public UnityBuildInfo? WindowsInfo { get; private set; }
        public UnityBuildInfo? LinuxInfo { get; private set; }
        public UnityBuildInfo? MacOsInfo { get; private set; }

        public UnityBuild(string repositoryPath, string? id, UnityVersion version)
        {
            Id = id;
            Version = version;

            if (Version.Major >= 5 && Id == null)
            {
                throw new Exception("Hash cannot be null after 5.x");
            }

            ShortVersion = Version.ToStringWithoutType();
            NuGetVersion = Version.Type == UnityVersionType.Final
                ? new NuGetVersion(Version.Major, Version.Minor, Version.Build)
                : new NuGetVersion(Version.Major, Version.Minor, Version.Build, Version.Type switch
                {
                    UnityVersionType.Alpha => "alpha",
                    UnityVersionType.Beta => "beta",
                    UnityVersionType.China => "china",
                    UnityVersionType.Final => "final",
                    UnityVersionType.Patch => "patch",
                    UnityVersionType.Experimental => "experimental",
                    _ => throw new ArgumentOutOfRangeException(nameof(Version.Type), Version.Type,
                        "Invalid Version.Type for " + Version),
                } + "." + Version.TypeNumber);

            var versionName = Version.Type == UnityVersionType.Final ? ShortVersion : Version.ToString();
            var zipName = $"{versionName}.zip";
            UnityLibsZipFilePath = Path.Combine(repositoryPath, "libraries", zipName);
            MonoPath = Path.Combine(repositoryPath, "mono", versionName);
            AndroidPath = Path.Combine(repositoryPath, "android", versionName);
            CorlibZipPath = Path.Combine(repositoryPath, "corlibs", zipName);
            LibIl2CppSourceZipPath = Path.Combine(repositoryPath, "libil2cpp-source", zipName);
            NuGetPackagePath = Path.Combine(repositoryPath, "packages", $"{NuGetVersion}.nupkg");
            InfoCacheDir = Path.Combine(repositoryPath, "versions", $"{id}");

            WindowsInfo = ReadInfo("win");

            if (Version >= _firstLinuxVersion)
            {
                LinuxInfo = ReadInfo("linux");
            }

            MacOsInfo = ReadInfo("osx");
        }

        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _downloadLock = new(2, 2);

        private static readonly UnityVersion _firstLinuxVersion = new(2018, 1, 5);

        // First modular version where own native player is included in the default installer
        private static readonly UnityVersion _firstMergedModularVersion = new(5, 4);
        private static readonly UnityVersion _firstLibIl2CppVersion = new(5, 0, 2);

        // TODO: Might need to define more DLLs? This should be enough for basic unhollowing.
        private static readonly string[] _importantCorlibs =
        {
            "Microsoft.CSharp",
            "Mono.Posix",
            "Mono.Security",
            "mscorlib",
            "Facades/netstandard",
            "System.Configuration",
            "System.Core",
            "System.Data",
            "System",
            "System.Net.Http",
            "System.Numerics",
            "System.Runtime.Serialization",
            "System.Security",
            "System.Xml",
            "System.Xml.Linq",
        };

        public bool HasLinuxEditor => LinuxInfo is not null;
        public bool HasModularPlayer => Version >= _firstMergedModularVersion;
        public bool IsMonolithic => Version.IsMonolithic();
        public bool HasLibIl2Cpp => Version >= _firstLibIl2CppVersion;
        public bool IsLegacyDownload => Id == null || Version.Major < 5;

        public bool NeedsInfoFetch { get; private set; }

        private UnityBuildInfo? ReadInfo(string variation)
        {
            if (!Directory.Exists(InfoCacheDir))
            {
                NeedsInfoFetch = true;
                return null;
            }

            var path = Path.Combine(InfoCacheDir, $"{variation}.ini");
            try
            {
                var variationIni = File.ReadAllText(path);
                var info = UnityBuildInfo.Parse(variationIni);
                if (info.Unity.Version != null && !info.Unity.Version.Equals(Version))
                {
                    throw new Exception();
                }

                return info;
            }
            catch (Exception)
            {
                NeedsInfoFetch = true;
                return null;
            }
        }

        public async Task FetchInfoAsync(CancellationToken cancellationToken)
        {
            if (!NeedsInfoFetch)
            {
                return;
            }

            async Task<UnityBuildInfo?> FetchVariation(string variation)
            {
                var variationUrl = BaseDownloadUrl + $"unity-{Version}-{variation}.ini";
                string variationIni;
                try
                {
                    Log.Information("Fetching {Variation} info for {Version} from {Url}", variation, Version, variationUrl);
                    variationIni = await _httpClient.GetStringAsync(variationUrl, cancellationToken);
                }
                catch (HttpRequestException hre) when (hre.StatusCode == HttpStatusCode.Forbidden)
                {
                    Log.Warning("Could not fetch {Variation} info for {Version} from {Url}. Got 'Access forbidden'",
                        variation, Version, variationUrl);
                    return null;
                }

                var info = UnityBuildInfo.Parse(variationIni);
                if (info.Unity.Version != null && !info.Unity.Version.Equals(Version))
                {
                    throw new Exception(
                        $"Build info version is invalid (expected {Version}, got {info.Unity.Version})");
                }

                Directory.CreateDirectory(InfoCacheDir);
                await File.WriteAllTextAsync(Path.Combine(InfoCacheDir, $"{variation}.ini"), variationIni,
                    cancellationToken);
                return info;
            }

            WindowsInfo = await FetchVariation("win");
            MacOsInfo = await FetchVariation("osx");
            LinuxInfo = await FetchVariation("linux");
            NeedsInfoFetch = false;
        }

        private string GetDownloadFile()
        {
            var isLegacyDownload = Id == null || Version.Major < 5;
            var editorDownloadPrefix = isLegacyDownload ? "UnitySetup-" : "UnitySetup64-";

            if (LinuxInfo != null)
            {
                return LinuxInfo.Unity.Url;
            }

            if (MacOsInfo != null && HasModularPlayer)
            {
                return MacOsInfo.Unity.Url;
            }

            if (WindowsInfo != null)
            {
                return WindowsInfo.Unity.Url;
            }

            return $"{editorDownloadPrefix}{ShortVersion}.exe";
        }


        public async Task MineAsync(CancellationToken cancellationToken)
        {
            var isLegacyDownload = Id == null || Version.Major < 5;

            var downloadFile = GetDownloadFile();
            var monoDownloadFile = GetDownloadFile();

            var monoDownloadUrl = BaseDownloadUrl + downloadFile;
            var corlibDownloadUrl = "";
            // For specific versions, the installer has no players at all
            // So for corlib, download both the installer and the support module
            if (!IsMonolithic && !HasModularPlayer)
            {
                corlibDownloadUrl = monoDownloadUrl;
                monoDownloadUrl = BaseDownloadUrl + monoDownloadFile;
            }

            var androidDownloadUrl =
                (LinuxInfo == null && MacOsInfo == null) ||
                Version.IsMonolithic() // TODO make monolithic handling better
                    ? null
                    : BaseDownloadUrl + (LinuxInfo ?? MacOsInfo)!.Android!.Url;

            var tmpDirectory = Path.Combine(Path.GetTempPath(), "UnityDataMiner", Version.ToString());
            Directory.CreateDirectory(tmpDirectory);

            var managedDirectory = Path.Combine(tmpDirectory, "managed");
            var corlibDirectory = Path.Combine(tmpDirectory, "corlib");
            var libil2cppSourceDirectory = Path.Combine(tmpDirectory, "libil2cpp-source");
            var androidDirectory = Path.Combine(tmpDirectory, "android");

            var monoArchivePath = Path.Combine(tmpDirectory, Path.GetFileName(monoDownloadUrl));
            var corlibArchivePath = !IsMonolithic && !HasModularPlayer
                ? Path.Combine(tmpDirectory, Path.GetFileName(corlibDownloadUrl))
                : monoArchivePath;
            var androidArchivePath = androidDownloadUrl == null
                ? null
                : Path.Combine(tmpDirectory, Path.GetFileName(androidDownloadUrl));
            var libil2cppSourceArchivePath = Path.Combine(tmpDirectory, Path.GetFileName(downloadFile));

            var assetCollection = new DownloadableAssetCollection();

            // main downloads
            monoArchivePath = assetCollection.AddAsset(monoDownloadUrl, monoArchivePath);
            corlibArchivePath = !string.IsNullOrEmpty(corlibDownloadUrl) 
                ? assetCollection.AddAsset(corlibDownloadUrl, corlibArchivePath)
                : monoArchivePath;
            androidArchivePath = androidDownloadUrl is not null
                ? assetCollection.AddAsset(androidDownloadUrl, androidArchivePath!)
                : androidArchivePath;

            // extra Mono pack downloads
            // Note: we want to avoid Windows downloads, because 7z can't extract some of those.
            // We also want to prefer downloading specific platform support bundles because those are significantly smaller.
            var monoWinBuild = LinuxInfo?.WindowsMono ?? MacOsInfo?.WindowsMono;
            var monoLinuxBuild = MacOsInfo?.LinuxMono ?? WindowsInfo?.LinuxMono;
            var monoMacBuild = LinuxInfo?.MacMono ?? WindowsInfo?.MacMono;

            string? AddMonoBuildDownload(UnityBuildInfo.Module? module, string osName)
            {
                if (module is null)
                {
                    // TODO: try to fall back to the main pack if unavailable?
                    Log.Warning("[{Version}] Could not get URL for Mono pack for {OS}", Version, osName);
                    return null;
                }

                return assetCollection.AddAsset(BaseDownloadUrl + module.Url,
                    Path.Combine(tmpDirectory, $"{osName}-{Path.GetFileName(module.Url)}"));
            }

            var monoWinArchive = AddMonoBuildDownload(monoWinBuild, "windows");
            var monoLinuxArchive = AddMonoBuildDownload(monoLinuxBuild, "linux");
            var monoMacArchive = AddMonoBuildDownload(monoMacBuild, "macos");

            try
            {
                await assetCollection.DownloadAssetsAsync(DownloadAsync, Version, null, cancellationToken);

                // process android
                if (androidDownloadUrl != null && !Directory.Exists(AndroidPath))
                {
                    Log.Information("[{Version}] Extracting android binaries", Version);
                    using var stopwatch = new AutoStopwatch();

                    var archiveDirectory =
                        Path.Combine(tmpDirectory, Path.GetFileNameWithoutExtension(androidArchivePath)!);

                    const string libs = "Variations/il2cpp/Release/Libs";
                    const string symbols = "Variations/il2cpp/Release/Symbols";

                    await ExtractAsync(androidArchivePath!, archiveDirectory,
                        new[] { $"./{libs}/*/libunity.so", $"./{symbols}/*/libunity.sym.so" }, cancellationToken, false);

                    Directory.CreateDirectory(androidDirectory);

                    IEnumerable<string> directories = Directory.GetDirectories(Path.Combine(archiveDirectory, libs));

                    var hasSymbols = Version > new UnityVersion(5, 3, 5, UnityVersionType.Final, 1);

                    if (hasSymbols)
                    {
                        directories =
                            directories.Concat(Directory.GetDirectories(Path.Combine(archiveDirectory, symbols)));
                    }

                    foreach (var directory in directories)
                    {
                        var directoryInfo =
                            Directory.CreateDirectory(Path.Combine(androidDirectory, Path.GetFileName(directory)));
                        foreach (var file in Directory.GetFiles(directory))
                        {
                            File.Copy(file, Path.Combine(directoryInfo.FullName, Path.GetFileName(file)), true);
                        }
                    }

                    if (hasSymbols)
                    {
                        foreach (var directory in Directory.GetDirectories(androidDirectory))
                        {
                            await EuUnstrip.UnstripAsync(Path.Combine(directory, "libunity.so"),
                                Path.Combine(directory, "libunity.sym.so"), cancellationToken);
                        }
                    }

                    Directory.CreateDirectory(AndroidPath);

                    foreach (var directory in Directory.GetDirectories(androidDirectory))
                    {
                        ZipFile.CreateFromDirectory(directory,
                            Path.Combine(AndroidPath, Path.GetFileName(directory) + ".zip"));
                    }

                    Log.Information("[{Version}] Extracted android binaries in {Time}", Version, stopwatch.Elapsed);
                }

                // process libil2cpp
                if (!File.Exists(LibIl2CppSourceZipPath))
                {
                    Log.Information("[{Version}] Extracting libil2cpp source code", Version);
                    using (var stopwatch = new AutoStopwatch())
                    {
                        // TODO: find out if the path changes in different versions
                        var libil2cppSourcePath = HasLinuxEditor switch
                        {
                            true => "Editor/Data/il2cpp/libil2cpp",
                            false when HasModularPlayer => "./Unity/Unity.app/Contents/il2cpp/libil2cpp",
                            false => "Editor/Data/il2cpp/libil2cpp",
                        };
                        await ExtractAsync(libil2cppSourceArchivePath, libil2cppSourceDirectory,
                            new[] { $"{libil2cppSourcePath}/**" }, cancellationToken, false);
                        var zipDir = Path.Combine(libil2cppSourceDirectory, libil2cppSourcePath);
                        if (!Directory.Exists(zipDir) || Directory.GetFiles(zipDir).Length <= 0)
                        {
                            throw new Exception("LibIl2Cpp source code directory is empty");
                        }

                        File.Delete(LibIl2CppSourceZipPath);
                        ZipFile.CreateFromDirectory(zipDir, LibIl2CppSourceZipPath);

                        Log.Information("[{Version}] Extracted libil2cpp source code in {Time}", Version,
                            stopwatch.Elapsed);
                    }
                }

                async Task ExtractManagedDir()
                {
                    bool Exists() => Directory.Exists(managedDirectory) &&
                                     Directory.GetFiles(managedDirectory, "*.dll").Length > 0;

                    if (Exists())
                    {
                        return;
                    }

                    // TODO: Clean up this massive mess
                    var monoPath = (Version.IsMonolithic(), isLegacyDownload) switch
                    {
                        (true, true) when Version.Major == 4 && Version.Minor >= 5 =>
                            "Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment/Data/Managed",
                        (true, true) => "Data/PlaybackEngines/windows64standaloneplayer/Managed",
                        (true, false) =>
                            "Editor/Data/PlaybackEngines/windowsstandalonesupport/Variations/win64_nondevelopment_mono/Data/Managed",
                        (false, true) => throw new Exception(
                            "Release can't be both legacy and modular at the same time"),
                        (false, false) when HasLinuxEditor =>
                            $"Editor/Data/PlaybackEngines/LinuxStandaloneSupport/Variations/linux64{(Version >= new UnityVersion(2021, 2) ? "_player" : "_withgfx")}_nondevelopment_mono/Data/Managed",
                        (false, false) when !HasLinuxEditor && HasModularPlayer =>
                            $"./Unity/Unity.app/Contents/PlaybackEngines/MacStandaloneSupport/Variations/macosx64_nondevelopment_mono/Data/Managed",
                        (false, false) => "./Variations/win64_nondevelopment_mono/Data/Managed",
                    };

                    await ExtractAsync(monoArchivePath, managedDirectory, new[] { $"{monoPath}/*.dll" },
                        cancellationToken);

                    if (!Exists())
                    {
                        throw new Exception("Managed directory is empty");
                    }
                }

                // process unity libs
                if (!File.Exists(UnityLibsZipFilePath))
                {
                    Log.Information("[{Version}] Extracting mono libraries", Version);
                    using (var stopwatch = new AutoStopwatch())
                    {
                        await ExtractManagedDir();
                        ZipFile.CreateFromDirectory(managedDirectory, UnityLibsZipFilePath);
                        Log.Information("[{Version}] Extracted mono libraries in {Time}", Version, stopwatch.Elapsed);
                    }
                }

                // process corlibs
                if (!File.Exists(CorlibZipPath))
                {
                    using (var stopwatch = new AutoStopwatch())
                    {
                        // TODO: Maybe grab both 2.0 and 4.5 DLLs for < 2018 monos
                        var corlibPath = isLegacyDownload switch
                        {
                            true => "Data/Mono/lib/mono/2.0",
                            false when HasLinuxEditor || !HasModularPlayer =>
                                "Editor/Data/MonoBleedingEdge/lib/mono/4.5",
                            false => "./Unity/Unity.app/Contents/MonoBleedingEdge/lib/mono/4.5",
                        };

                        await ExtractAsync(corlibArchivePath, corlibDirectory,
                            _importantCorlibs.Select(s => $"{corlibPath}/{s}.dll").ToArray(), cancellationToken);

                        if (!Directory.Exists(corlibDirectory) ||
                            Directory.GetFiles(corlibDirectory, "*.dll").Length <= 0)
                        {
                            throw new Exception("Corlibs directory is empty");
                        }

                        File.Delete(CorlibZipPath);
                        ZipFile.CreateFromDirectory(corlibDirectory, CorlibZipPath);

                        Log.Information("[{Version}] Extracted corlibs in {Time}", Version, stopwatch.Elapsed);
                    }
                }

                // generate nuget package
                if (!File.Exists(NuGetPackagePath))
                {
                    Log.Information("[{Version}] Creating NuGet package for mono libraries", Version);
                    using (var stopwatch = new AutoStopwatch())
                    {
                        await ExtractManagedDir();
                        CreateNuGetPackage(managedDirectory);
                        Log.Information("[{Version}] Created NuGet package for mono libraries in {Time}", Version,
                            stopwatch.Elapsed);
                    }
                }

                // process Mono builds
                if (!Directory.Exists(MonoPath))
                {
                    Log.Information("[{Version}] Packaging Mono binaries", Version);
                    using var stopwatch = new AutoStopwatch();

                    var monoBaseDir = Path.Combine(tmpDirectory, "mono");
                    var winBaseDir = Path.Combine(monoBaseDir, "win");
                    var linuxBaseDir = Path.Combine(monoBaseDir, "linux");
                    var macBaseDir = Path.Combine(monoBaseDir, "mac");

                    // first, Windows
                    if (monoWinArchive is not null)
                    {
                        Log.Information("[{Version}] Processing Windows", Version);

                        // extract all of the Mono variants
                        await ExtractAsync(monoWinArchive, winBaseDir, 
                            ["./Variations/*_player_nondevelopment_mono/Mono*/**"],
                            cancellationToken, flat: false);

                        // Windows is nice and easy, we just want to pack up all the subdirs of *_player_nondevelopment_mono
                        // They contain fully self-contained Mono installs minus the corelib, which we extract above.
                        // The actual executable binaries are in /EmbedRuntime
                        foreach (var playerDir in Directory.EnumerateDirectories(Path.Combine(winBaseDir, "Variations")))
                        {
                            var arch = Path.GetFileName(playerDir).Replace("_player_nondevelopment_mono", "");
                            if (!arch.Contains("win"))
                            {
                                arch = "win_" + arch;
                            }

                            foreach (var monoDir in Directory.EnumerateDirectories(playerDir))
                            {
                                var monoName = Path.GetFileName(monoDir);

                                // rename EmbedRuntime to just runtime for consistency across platforms
                                var runtimeDir = Path.Combine(monoDir, "runtime");
                                if (Directory.Exists(runtimeDir))
                                {
                                    Directory.Delete(runtimeDir, true);
                                }
                                Directory.Move(Path.Combine(monoDir, "EmbedRuntime"), runtimeDir);
                                ZipFile.CreateFromDirectory(monoDir, Path.Combine(monoBaseDir, $"{arch}_{monoName}.zip"));
                            }
                        }
                    }

                    // next, Linux
                    if (monoLinuxArchive is not null)
                    {
                        Log.Information("[{Version}] Processing Linux", Version);

                        await ExtractAsync(monoLinuxArchive, linuxBaseDir,
                            ["./Variations/*_player_nondevelopment_mono/Data/Mono*/**"],
                            cancellationToken, flat: false);

                        // Linux is mostly similar to Windows, except that the runtime binaries are in x86_64 instead of EmbedRuntime
                        // Presumably, if non-x64 support is added, the runtime files would end up in folders named for the arch, but
                        // Unity doesn't support any of those right now, so who knows.
                        foreach (var playerDir in Directory.EnumerateDirectories(Path.Combine(linuxBaseDir, "Variations")))
                        {
                            var arch = Path.GetFileName(playerDir).Replace("_player_nondevelopment_mono", "");
                            if (!arch.Contains("linux"))
                            {
                                arch = "linux_" + arch;
                            }

                            foreach (var monoDir in Directory.EnumerateDirectories(Path.Combine(playerDir, "Data")))
                            {
                                var monoName = Path.GetFileName(monoDir);

                                // rename the runtime directory for consistency

                                var runtimeDir = Path.Combine(monoDir, "runtime");
                                if (Directory.Exists(runtimeDir))
                                {
                                    Directory.Delete(runtimeDir, true);
                                }
                                Directory.Move(Path.Combine(monoDir, "x86_64"), runtimeDir);
                                ZipFile.CreateFromDirectory(monoDir, Path.Combine(monoBaseDir, $"{arch}_{monoName}.zip"));
                            }
                        }
                    }
                    
                    // finally, MacOS
                    if (monoMacArchive is not null)
                    {
                        Log.Information("[{Version}] Processing MacOS", Version);

                        await ExtractAsync(monoMacArchive, macBaseDir,
                            [
                                "./Mono*/**", // this contains the configuration
                                "./Variations/*_player_nondevelopment_mono/UnityPlayer.app/Contents/Frameworks/lib*" // this contains the actual runtime
                                // note: we filter to the lib prefix to avoid extracting UnityPlayer.dylib
                            ],
                            cancellationToken, flat: false);

                        // MacOS is the messiest. There's only one copy of the config files, but several of the runtime, in a very unusual structure.
                        // We'll just have to cope though.
                        foreach (var monoConfigDir in Directory.EnumerateDirectories(macBaseDir, "Mono*"))
                        {
                            var monoName = Path.GetFileName(monoConfigDir);
                            var runtimeDir = Path.Combine(monoConfigDir, "runtime");

                            foreach (var playerDir in Directory.EnumerateDirectories(Path.Combine(macBaseDir, "Variations")))
                            {
                                var arch = Path.GetFileName(playerDir).Replace("_player_nondevelopment_mono", "");
                                if (!arch.Contains("macos"))
                                {
                                    arch = "macos_" + arch;
                                }

                                if (Directory.Exists(runtimeDir))
                                {
                                    Directory.Delete(runtimeDir, true);
                                }

                                Directory.Move(Path.Combine(playerDir, "UnityPlayer.app", "Contents", "Frameworks"), runtimeDir);
                                ZipFile.CreateFromDirectory(monoConfigDir, Path.Combine(monoBaseDir, $"{arch}_{monoName}.zip"));
                            }
                        }
                    }

                    // we've created all of the zip files, move them into place
                    Directory.CreateDirectory(MonoPath);
                    foreach (var zip in Directory.EnumerateFiles(monoBaseDir, "*.zip"))
                    {
                        File.Move(zip, Path.Combine(MonoPath, Path.GetFileName(zip)));
                    }

                    Log.Information("[{Version}] Mono binaries packaged in {Time}", Version,
                        stopwatch.Elapsed);
                }
            }
            finally
            {
                Directory.Delete(tmpDirectory, true);
            }
        }

        public Task ExecuteJobsAsync(ImmutableArray<MinerJob> jobs, CancellationToken cancellationToken)
        {
            var plan = JobPlanner.Plan(jobs, this, cancellationToken);
            if (plan is null) return Task.CompletedTask; // if we failed to find a plan for a problematic reason, it's already been logged
            return ExecutePlan(plan, cancellationToken);
        }

        private async Task ExecutePlan(JobPlanner.JobPlan plan, CancellationToken cancellationToken)
        {
            var jobsToStart = plan.Jobs.ToBuilder();

            var tmpDirectory = Path.Combine(Path.GetTempPath(), "UnityDataMiner", Version.ToString());
            Directory.CreateDirectory(tmpDirectory);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            try
            {
                var targetPaths = new string[plan.Packages.Length];
                var downloadTasks = new Task?[plan.Packages.Length];

                for (var i = 0; i < plan.Packages.Length; i++)
                {
                    var package = plan.Packages[i];
                    var targetName = Path.GetFileName(package.Url);
                    var arcPath = Path.Combine(tmpDirectory, i + "-" + targetName);
                    targetPaths[i] = arcPath;
                    // start all download tasks simultaneously to try to fully saturate our allowed parallel downlads
                    downloadTasks[i] = DownloadAndPreExtractAsync(BaseDownloadUrl + package.Url, arcPath, cts.Token);
                }

                var jobTasks = new List<Task>(jobsToStart.Count);
                var downloadedPackages = new HashSet<int>(plan.Packages.Length);

                // our main download processing loop
                while (downloadTasks.Any(t => t is not null))
                {
                    var completed = await Task.WhenAny(downloadTasks.Where(t => t is not null)!);

                    var i = Array.IndexOf(downloadTasks, completed);

                    switch (completed.Status)
                    {
                        default:
                        case TaskStatus.Created:
                        case TaskStatus.WaitingForActivation:
                        case TaskStatus.WaitingToRun:
                        case TaskStatus.Running:
                        case TaskStatus.WaitingForChildrenToComplete:
                            // none of these are completion states, it should never happen
                            continue;

                        case TaskStatus.Canceled:
                            // download was cancelled, which means that our own CT was set. Wait for everything, and (inevitably) bail.
                            // First, lets cancel everything though. Want to bail as quickly as possible.
                            await cts.CancelAsync();
                            await Task.WhenAll(jobTasks.Concat(downloadTasks.Where(t => t is not null))!);
                            break;

                        case TaskStatus.Faulted:
                            // task faulted; check the exception and maybe retry, otherwise do the same as when cancelled
                            if (completed.Exception is {
                                InnerExceptions: [ IOException {
                                    InnerException: SocketException { SocketErrorCode: SocketError.ConnectionReset }
                                }]
                            })
                            {
                                // we want to retry
                                static async Task RetryDownload(UnityBuild @this, string url, string packagePath, CancellationToken cancellationToken)
                                {
                                    Log.Warning("[{Version}] Failed to download {Url}, waiting 5 seconds before retrying...", @this.Version, url);
                                    await Task.Delay(5000, cancellationToken);
                                    await @this.DownloadAndPreExtractAsync(@this.BaseDownloadUrl + url, packagePath, cancellationToken);
                                }

                                downloadTasks[i] = RetryDownload(this, plan.Packages[i].Url, targetPaths[i], cts.Token);
                            }
                            else
                            {
                                // unknown exception, bail out
                                goto case TaskStatus.Canceled;
                            }
                            break;

                        case TaskStatus.RanToCompletion:
                            // the download completed. Lets await it to connect up the invocation, then start any jobs that can now start.
                            downloadTasks[i] = null; // clear the task so we don't hit it again
                            _ = downloadedPackages.Add(i);
                            await completed;

                            var incrNo = 0;
                            for (var j = 0; j < jobsToStart.Count; j++)
                            {
                                var (needs, job) = jobsToStart[j];

                                if (job.RunIncrementally)
                                {
                                    // incremental job
                                    if (!needs.Contains(i))
                                    {
                                        // this package isn't one that this job cares about
                                        continue;
                                    }

                                    // this job cares about this package; invoke for it

                                    // create its temp dir
                                    var localTmpDir = Path.Combine(tmpDirectory, $"i-{i}-{incrNo++}");
                                    Directory.CreateDirectory(localTmpDir);

                                    var incPackage = plan.Packages[i];
                                    var incTargetPath = targetPaths[i];

                                    Log.Debug("[{Version}] Starting job {Job} incrementally for {Asset}", Version, job.Name, Path.GetFileName(incPackage.Url));

                                    // start the job
                                    jobTasks.Add(
                                        job.ExtractFromAssets(this, localTmpDir,
                                            [incPackage.Package],
                                            [incTargetPath],
                                            cts.Token));
                                }
                                else
                                {
                                    // normal, non-incremental job
                                    if (!needs.All(downloadedPackages.Contains))
                                    {
                                        // not all of this jobs requirements are downloaded, keep checking
                                        continue;
                                    }

                                    // we have everything we need, set up the arrays we need to pass to the job
                                    // first, remove it from our list though
                                    jobsToStart.RemoveAt(j--);

                                    var packages = new UnityPackage[needs.Length];
                                    var archivePaths = new string[needs.Length];

                                    for (var k = 0; k < needs.Length; k++)
                                    {
                                        var jobIndex = needs[k];
                                        packages[k] = plan.Packages[jobIndex].Package;
                                        archivePaths[k] = targetPaths[jobIndex];
                                    }

                                    // give it its own temp dir
                                    var localTmpDir = Path.Combine(tmpDirectory, jobsToStart.Count.ToString());
                                    Directory.CreateDirectory(localTmpDir);

                                    Log.Debug("[{Version}] Starting job {Job} (local dir: {RemainingJobs})", Version, job.Name, jobsToStart.Count);

                                    // and start the job
                                    jobTasks.Add(
                                        job.ExtractFromAssets(this, localTmpDir,
                                            ImmutableCollectionsMarshal.AsImmutableArray(packages),
                                            ImmutableCollectionsMarshal.AsImmutableArray(archivePaths),
                                            cts.Token));
                                }
                            }
                            break;
                    }
                }

                // we've now downloaded all of our assets, and are just waiting for the jobs to complete
                await Task.WhenAll(jobTasks);
            }
            finally
            {
                Directory.Delete(tmpDirectory, true);
            }
        }

        private async Task DownloadAndPreExtractAsync(string downloadUrl, string archivePath, CancellationToken cancellationToken)
        {
            await DownloadAsync(downloadUrl, archivePath, cancellationToken);
            // run a pre-extraction on the archive, if it's one that needs it, to avoid data races later
            Log.Information("[{Version}] Pre-extracting {Archive}", Version, Path.GetFileName(archivePath));
            await ExtractAsync(archivePath, "", [], cancellationToken, firstStageOnly: true);
            Log.Information("[{Version}] Pre-extract complete", Version);
        }

        public async Task DownloadAsync(string downloadUrl, string archivePath, CancellationToken cancellationToken)
        {
            if (File.Exists(archivePath))
            {
                Log.Information("[{Version}] Skipping download because {File} exists", Version, archivePath);
            }
            else
            {
                using var stopwatch = new AutoStopwatch();
                try
                {
                    await _downloadLock.WaitAsync(cancellationToken);

                    Log.Information("[{Version}] Downloading {Url}", Version, downloadUrl);
                    stopwatch.Restart();

                    await using (var stream = await _httpClient.GetStreamAsync(downloadUrl, cancellationToken))
                    await using (var fileStream = File.OpenWrite(archivePath + ".part"))
                    {
                        await stream.CopyToAsync(fileStream, cancellationToken);
                    }
                }
                finally
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _downloadLock.Release();
                    }
                }

                // do move outside lock in case it takes a lot of time
                File.Move(archivePath + ".part", archivePath);

                Log.Information("[{Version}] Downloaded {Url} in {Time}", Version, downloadUrl, stopwatch.Elapsed);
            }
        }

        // TODO: it's probably a good idea to lock around extraction for the double-extract cases
        public async Task ExtractAsync(string archivePath, string destinationDirectory, string[] filter,
            CancellationToken cancellationToken, bool flat = true, bool firstStageOnly = false)
        {
            var archiveDirectory = Path.Combine(Path.GetDirectoryName(archivePath)!,
                Path.GetFileNameWithoutExtension(archivePath));
            var extension = Path.GetExtension(archivePath);

            switch (extension)
            {
                case ".pkg":
                {
                    const string payloadName = "Payload~";
                    var payloadPath = Path.Combine(archiveDirectory, payloadName);
                    if (!File.Exists(payloadPath))
                    {
                        await SevenZip.ExtractAsync(archivePath, archiveDirectory, [payloadName], true,
                            cancellationToken);
                    }
                    if (!firstStageOnly)
                    {
                        await SevenZip.ExtractAsync(payloadPath, destinationDirectory,
                            filter, flat, cancellationToken);
                    }

                    break;
                }

                case ".exe":
                {
                    if (!firstStageOnly)
                    {
                        await SevenZip.ExtractAsync(archivePath, destinationDirectory, filter, flat, cancellationToken);
                    }

                    break;
                }

                case ".xz":
                {
                    string payloadName = Path.GetFileNameWithoutExtension(archivePath);
                    var payloadPath = Path.Combine(archiveDirectory, payloadName);
                    if (!File.Exists(payloadPath))
                    {
                        await SevenZip.ExtractAsync(archivePath, archiveDirectory, [payloadName], true,
                            cancellationToken);
                    }
                    if (!firstStageOnly)
                    {
                        await SevenZip.ExtractAsync(payloadPath, destinationDirectory,
                            filter, flat, cancellationToken);
                    }

                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(extension), extension, "Unrecognized archive type");
            }
        }

        private void CreateNuGetPackage(string pkgDir)
        {
            foreach (var file in Directory.EnumerateFiles(pkgDir, "*.dll"))
                AssemblyPublicizer.Publicize(file, file, new AssemblyPublicizerOptions { Strip = true });

            var deps = new[] { "net35", "net45", "netstandard2.0" };

            var meta = new ManifestMetadata
            {
                Id = "UnityEngine.Modules",
                Authors = new[] { "Unity" },
                Version = NuGetVersion,
                Description = "UnityEngine modules",
                DevelopmentDependency = true,
                DependencyGroups = deps.Select(d =>
                    new PackageDependencyGroup(NuGetFramework.Parse(d), Array.Empty<PackageDependency>()))
            };

            var builder = new PackageBuilder(true);
            builder.PopulateFiles(pkgDir, deps.Select(d => new ManifestFile
            {
                Source = "*.dll",
                Target = $"lib/{d}"
            }));
            builder.Populate(meta);
            using var fs = File.Create(NuGetPackagePath);
            builder.Save(fs);
        }

        public async Task UploadNuGetPackageAsync(string sourceUrl, string apikey)
        {
            Log.Information("[{Version}] Pushing NuGet package", Version);
            var repo = Repository.Factory.GetCoreV3(sourceUrl);
            var updateResource = await repo.GetResourceAsync<PackageUpdateResource>();
            await updateResource.Push(new[] { NuGetPackagePath },
                null,
                2 * 60,
                false,
                s => apikey,
                s => null,
                false,
                true,
                null,
                NullLogger.Instance);
            Log.Information("[{Version}] Pushed NuGet package", Version);
        }
    }
}
