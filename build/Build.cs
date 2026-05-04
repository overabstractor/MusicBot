using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;

// Nuke.Common 8.x usa BinaryFormatter para clonar los settings de todas las herramientas
// (DotNetTasks, NpmTasks, etc.), lo que falla en .NET 9 donde fue removido.
// Workaround: invocar dotnet y npm directamente via ProcessTasks.StartProcess.

class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Build configuration (Debug | Release)")]
    readonly string Configuration = "Release";

    [Parameter("Target runtime para publish (ej. win-x86, win-x64)")]
    readonly string Runtime = "win-x86";

    [Parameter("Versión del paquete (ej. 1.0.0)")]
    readonly string Version = "0.0.1";

    [Solution(SuppressBuildProjectCheck = true)]
    readonly Solution Solution = default!;

    AbsolutePath ClientAppDir => RootDirectory / "src" / "MusicBot.Web" / "clientapp";
    AbsolutePath WwwRootDir   => RootDirectory / "src" / "MusicBot.Web" / "wwwroot";
    AbsolutePath PublishDir   => RootDirectory / "publish" / Runtime;
    AbsolutePath ArtifactsDir => RootDirectory / "artifacts";

    Project DesktopProject => Solution.GetProject("MusicBot.Desktop")!;

    void Npm(string args) =>
        ProcessTasks.StartProcess("npm", args, workingDirectory: ClientAppDir)
            .AssertZeroExitCode();

    void DotNet(string args, AbsolutePath? workDir = null) =>
        ProcessTasks.StartProcess("dotnet", args, workingDirectory: workDir ?? RootDirectory)
            .AssertZeroExitCode();

    /// Limpia directorios de salida (publish + artifacts).
    Target Clean => _ => _
        .Before(Compile)
        .Executes(() =>
        {
            PublishDir.DeleteDirectory();
            ArtifactsDir.DeleteDirectory();
        });

    /// Instala dependencias npm del cliente React.
    Target RestoreNpm => _ => _
        .Executes(() => Npm("install"));

    /// Compila el cliente React con Vite. Vite ya escribe directo a wwwroot (outDir: ../wwwroot, emptyOutDir: true).
    Target BuildClient => _ => _
        .DependsOn(RestoreNpm)
        .Executes(() => Npm("run build"));

    /// Compila la solución .NET completa.
    Target Compile => _ => _
        .DependsOn(BuildClient)
        .Executes(() =>
            DotNet($"build {Solution} --configuration {Configuration}"));

    /// Ejecuta los tests unitarios. Bloquea la publicación si alguno falla.
    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
            DotNet("test src/MusicBot.Tests/MusicBot.Tests.csproj --no-build --configuration Release --logger trx"));

    /// Publica MusicBot.Desktop listo para distribución.
    Target Publish => _ => _
        .DependsOn(Test)
        .Executes(() =>
            DotNet($"publish {DesktopProject} " +
                   $"--configuration {Configuration} " +
                   $"--runtime {Runtime} " +
                   $"--self-contained " +
                   $"--output {PublishDir}"));

    /// Empaqueta el output publicado con Velopack (instalador + delta updates).
    Target Pack => _ => _
        .DependsOn(Publish)
        .Produces(ArtifactsDir / "*")
        .Executes(() =>
        {
            ArtifactsDir.CreateDirectory();
            ProcessTasks.StartProcess(
                "vpk",
                $"pack " +
                $"--packId MusicBot " +
                $"--packVersion {Version} " +
                $"--packDir {PublishDir} " +
                $"--outputDir {ArtifactsDir} " +
                $"--mainExe MusicBot.exe",
                workingDirectory: RootDirectory)
                .AssertZeroExitCode();
        });
}
