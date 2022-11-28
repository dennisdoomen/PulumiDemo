using System;
using System.IO;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Pulumi;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.HttpTasks;
using static Nuke.Common.Tooling.ProcessTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Pulumi.PulumiTasks;
using FileMode = System.IO.FileMode;

[ShutdownDotNetAfterServerBuild]
class Build : NukeBuild
{
    /// Support plugins are available for:
    /// - JetBrains ReSharper        https://nuke.build/resharper
    /// - JetBrains Rider            https://nuke.build/rider
    /// - Microsoft VisualStudio     https://nuke.build/visualstudio
    /// - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Default);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = true)]
    readonly Solution Solution;

    [GitVersion]
    readonly GitVersion GitVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    [Parameter]
    string AwsAccessKeyId = null;

    [Parameter]
    string AwsSecretAccessKey = null;

    [Parameter]
    string AwsRegion = "eu-west-1";

    [Parameter("The version of Pulumi to download and use")]
    string PulumiVersion = "v3.48.0";

    [Parameter]
    string PulumiConfigPassphrase = null;

    [Parameter("Tells the script to provision the environment using Pulumi")]
    bool Deploy;

    [Parameter("Tells the script to destroy the environment using Pulumi")]
    bool Destroy;
    
    [Parameter]
    string PulumiAccessToken;

    readonly Action<OutputType, string> RedirectErrorLogger = (_, s) => Log.Information(s);

    Target Clean => _ => _
        .Before(Restore)
        .OnlyWhenDynamic(() => IsServerBuild)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .EnableNoRestore());
        });

    Target DownloadPulumi => _ => _
        .After(Compile)
        .Executes(() =>
        {
            EnsureExistingDirectory(ArtifactsDirectory);

            string platformPostfix = EnvironmentInfo.IsLinux ? "linux-x64.tar.gz" : "windows-x64.zip";
            string filename = $"pulumi-{PulumiVersion}-{platformPostfix}";

            string urlToDownloadFrom =
                $"https://github.com/pulumi/pulumi/releases/download/{PulumiVersion}/{filename}";

            if (!File.Exists(ArtifactsDirectory / filename))
            {
                Log.Information($"Downloading Pulumi binaries from {urlToDownloadFrom}");
                HttpDownloadFile(urlToDownloadFrom, ArtifactsDirectory / filename, FileMode.Create, s =>
                {
                    s.Timeout = TimeSpan.FromSeconds(30);
                    return s;
                });
            }
            else
            {
                Log.Information($"Binaries for Pulumi {PulumiVersion} were already downloaded");
            }

            AbsolutePath binaryFolder = RootDirectory / "lib";
            AbsolutePath pulumiExe = binaryFolder / "pulumi" / "bin" / "pulumi.exe";

            if (!File.Exists(pulumiExe))
            {
                EnsureCleanDirectory(binaryFolder);
                Log.Information($"Unzipped Pulumi binaries to {binaryFolder}");
                Uncompress(ArtifactsDirectory / filename, binaryFolder);

                if (EnvironmentInfo.IsLinux)
                {
                    StartProcess("chmod", "777 ./lib/pulumi/pulumi").WaitForExit();
                }
            }
            else
            {
                Log.Information("The correct binaries were already unzipped");
            }

            Environment.SetEnvironmentVariable("Path",
                Environment.GetEnvironmentVariable("Path") + $";{binaryFolder}/pulumi/bin");
        });

    Target SignIntoPulumi => _ => _
        .DependsOn(DownloadPulumi)
        .Executes(() =>
        {
            var workingDirectory = Solution.GetProject("Deploy")!.Directory;
            
            if (PulumiAccessToken != null)
            {
                EnvironmentInfo.SetVariable("PULUMI_ACCESS_TOKEN", PulumiAccessToken);
                Pulumi("login", workingDirectory: workingDirectory);
            }
            else
            {
                Environment.SetEnvironmentVariable("PULUMI_CONFIG_PASSPHRASE", PulumiConfigPassphrase);
                Pulumi("login --local", workingDirectory: workingDirectory);
            }
        });

    Target SelectPulumiStack => _ => _
        .DependsOn(DownloadPulumi)
        .DependsOn(SignIntoPulumi)
        .Executes(() =>
        {
            var workingDirectory = Solution.GetProject("Deploy")!.Directory;

            try
            {
                PulumiStackInit(s => s
                    .SetOrganizationAndName(GitVersion.EscapedBranchName)
                    .SetProcessWorkingDirectory(workingDirectory));
            }
            catch
            {
                PulumiStackSelect(s => 
                    s.SetStackName(GitVersion.EscapedBranchName)
                        .SetProcessWorkingDirectory(workingDirectory));
            }
        });

    Target Provision => _ => _
        .DependsOn(DownloadPulumi)
        .DependsOn(SignIntoPulumi)
        .DependsOn(SelectPulumiStack)
        .DependsOn(Compile)
        .OnlyWhenStatic(() => AwsRegion != null)
        .OnlyWhenStatic(() => AwsSecretAccessKey != null)
        .OnlyWhenStatic(() => AwsAccessKeyId != null)
        .OnlyWhenStatic(() => !Destroy)
        .Executes(() =>
        {
            var workingDirectory = Solution.GetProject("Deploy")!.Directory;

            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", AwsAccessKeyId);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", AwsSecretAccessKey);
            Environment.SetEnvironmentVariable("AWS_REGION", AwsRegion);
            Environment.SetEnvironmentVariable("Configuration", Configuration);
            Environment.SetEnvironmentVariable("Root", SourceDirectory);

            if (Deploy)
            {
                PulumiUp(s => s
                    .SetYes(true)
                    .SetSkipPreview(true)
                    .SetRefresh(true)
                    .SetProcessWorkingDirectory(workingDirectory));
            }
            else if (!Destroy)
            {
                PulumiPreview(s => s
                    .SetRefresh(true)
                    .SetProcessWorkingDirectory(workingDirectory));
            }
        });
    Target Deprovision => _ => _
        .DependsOn(DownloadPulumi)
        .DependsOn(SignIntoPulumi)
        .DependsOn(SelectPulumiStack)
        .DependsOn(Compile)
        .After(Provision)
        .OnlyWhenStatic(() => AwsRegion != null)
        .OnlyWhenStatic(() => AwsSecretAccessKey != null)
        .OnlyWhenStatic(() => AwsAccessKeyId != null)
        .OnlyWhenStatic(() => Destroy && !Deploy)
        .Executes(() =>
        {
            var workingDirectory = Solution.GetProject("Deploy")!.Directory;

            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", AwsAccessKeyId);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", AwsSecretAccessKey);
            Environment.SetEnvironmentVariable("AWS_REGION", AwsRegion);
            Environment.SetEnvironmentVariable("Configuration", Configuration);
            Environment.SetEnvironmentVariable("Root", SourceDirectory);

            PulumiDestroy(s => s
                .SetYes(true)
                .SetSkipPreview(true)
                .SetRefresh(true)
                .SetProcessWorkingDirectory(workingDirectory));
        });

    Target Default => _ => _
        .DependsOn(Provision)
        .DependsOn(Deprovision);
}