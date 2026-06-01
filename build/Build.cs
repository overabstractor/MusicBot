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

    // Ruta explícita al .csproj de Desktop. Se usa en `dotnet publish` en lugar
    // del objeto Project porque su interpolación en la línea de comandos resolvía
    // a vacío → `dotnet publish` sin proyecto tomaba MusicBot.sln y publicaba
    // TODA la solución (incluido MusicBot.Tests + coverlet) al mismo --output,
    // contaminando el paquete de Velopack. Apuntar al csproj publica solo Desktop.
    AbsolutePath DesktopProjectFile =>
        RootDirectory / "src" / "MusicBot.Desktop" / "MusicBot.Desktop.csproj";

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
        {
            // Limpiar el directorio de publish ANTES de publicar. Sin esto, los
            // artefactos de MusicBot.Tests (MusicBot.Tests.dll, coverlet.*,
            // CodeCoverage/, TestResults/) que se filtran al directorio quedan
            // dentro del paquete de vpk: engorda el .nupkg a >100MB e introduce
            // DLLs de instrumentación de coverlet que disparan falsos positivos
            // de antivirus en máquinas de usuarios finales.
            PublishDir.CreateOrCleanDirectory();

            DotNet($"publish {DesktopProjectFile} " +
                   $"--configuration {Configuration} " +
                   $"--runtime {Runtime} " +
                   $"--self-contained " +
                   $"--output {PublishDir}");

            // Copiar el build de React junto al .exe. Al publicar SOLO el proyecto
            // Desktop, los static web assets de MusicBot.Web (su wwwroot) no se
            // emiten al output. El runtime los sirve desde <dir-del-exe>\wwwroot
            // (UseStaticFiles + MapFallbackToFile("index.html") en WebHost.cs);
            // sin esta copia la UI responde 404. WwwRootDir ya fue generado por
            // BuildClient (dependencia transitiva de este target vía Compile).
            WwwRootDir.Copy(PublishDir / "wwwroot", ExistsPolicy.MergeAndOverwrite);
        });

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
