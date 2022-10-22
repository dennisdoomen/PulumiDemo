using System;
using System.IO;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.Pulumi;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.HttpTasks;
using static Nuke.Common.Tooling.ProcessTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Pulumi.PulumiTasks;

[ShutdownDotNetAfterServerBuild]
class Build : NukeBuild
{
    /// Support plugins are available for:
    /// - JetBrains ReSharper        https://nuke.build/resharper
    /// - JetBrains Rider            https://nuke.build/rider
    /// - Microsoft VisualStudio     https://nuke.build/visualstudio
    /// - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.BuildContainer);

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
    string PulumiVersion = "v3.43.1";

    [Parameter]
    string PulumiConfigPassphrase = null;
    
    [Parameter]
    string PulumiAccessToken;

    readonly Action<OutputType, string> RedirectErrorLogger = (_, s) => Log.Information(s);

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
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

    Target BuildContainer => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DockerLogger = RedirectErrorLogger;

            DockerBuild(s => s
                .SetTag("dennis/minimalapi:" + GitVersion.EscapedBranchName)
                .SetBuildArg($"CONFIGURATION={Configuration}")
                .EnableNoCache()
                .EnableRm()
                .SetFile(SourceDirectory / "minimalapi.dockerfile")
                .SetPath(RootDirectory)
            );

            DockerSave(s => s
                .SetImages("dennis/minimalapi:" + GitVersion.EscapedBranchName)
                .SetOutput(ArtifactsDirectory / $"minimal_api_{GitVersion.EscapedBranchName}.tar.gz"));
        });

    Target DownloadPulumi => _ => _
        .Executes(() =>
        {
            EnsureExistingDirectory(ArtifactsDirectory);

            string platformPostfix = EnvironmentInfo.IsLinux ? "linux-x64.tar.gz" : "windows-x64.zip";
            string filename = $"pulumi-{PulumiVersion}-{platformPostfix}";

            string urlToDownloadFrom =
                $"https://github.com/pulumi/pulumi/releases/download/{PulumiVersion}/{filename}";

            if (!File.Exists(ArtifactsDirectory / filename))
            {
                Logger.Info($"Downloading Pulumi binaries from {urlToDownloadFrom}");
                HttpDownloadFile(urlToDownloadFrom, ArtifactsDirectory / filename);
            }
            else
            {
                Logger.Info($"Binaries for Pulumi {PulumiVersion} were already downloaded");
            }

            AbsolutePath binaryFolder = RootDirectory / "lib";
            AbsolutePath pulumiExe = binaryFolder / "pulumi" / "bin" / "pulumi.exe";

            if (!File.Exists(pulumiExe))
            {
                EnsureCleanDirectory(binaryFolder);
                Logger.Info($"Unzipped Pulumi binaries to {binaryFolder}");
                Uncompress(ArtifactsDirectory / filename, binaryFolder);

                if (EnvironmentInfo.IsLinux)
                {
                    StartProcess("chmod", "777 ./lib/pulumi/pulumi").WaitForExit();
                }
            }
            else
            {
                Logger.Info("The correct binaries were already unzipped");
            }

            Environment.SetEnvironmentVariable("Path",
                Environment.GetEnvironmentVariable("Path") + $";{binaryFolder}/pulumi/bin");
        });

    Target RunPulumiUp => _ => _
        .DependsOn(DownloadPulumi)
        .Requires(() => AwsRegion)
        .Requires(() => AwsSecretAccessKey)
        .Requires(() => AwsAccessKeyId)
        .Requires(() => PulumiConfigPassphrase)
        .Executes(() =>
        {
            var workingDirectory = Solution.GetProject("Deploy")!.Directory;
            
            Environment.SetEnvironmentVariable("PULUMI_CONFIG_PASSPHRASE", PulumiConfigPassphrase);
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", AwsAccessKeyId);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", AwsSecretAccessKey);
            Environment.SetEnvironmentVariable("AWS_REGION", AwsRegion);
            
            if (PulumiAccessToken != null)
            {
                EnvironmentInfo.SetVariable("PULUMI_ACCESS_TOKEN", PulumiAccessToken);
                Pulumi("login", workingDirectory: workingDirectory);
            }
            else
            {
                Pulumi("login --local", workingDirectory: workingDirectory);
            }
            
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
            
            // If we're running on the Sandbox, AWS resources could have disappeared. 
            PulumiPreview(s => s
                .SetRefresh(true)
                .SetProcessWorkingDirectory(workingDirectory));
        });
}