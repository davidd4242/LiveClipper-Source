using LiveClipper.Core;

public static class OllamaManagementEndpoints
{
    public sealed record LocationRequest(bool Confirmed, string? BaseDirectory,string? ExpectedPrivateRoot);
    public sealed record ActionRequest(bool Confirmed,string? ExpectedPrivateRoot=null);
    public sealed record SelectRequest(bool Confirmed, string Target);
    public sealed record InstallRequest(bool Confirmed, string? ExpectedVersion,string? ExpectedPrivateRoot);

    private sealed class InstallationLifetime(OllamaInstallationService service,OllamaManagedDaemon daemon,ILogger<InstallationLifetime> logger) : IHostedService
    {
        public Task StartAsync(CancellationToken token)=>Task.CompletedTask;
        public async Task StopAsync(CancellationToken token)
        {
            await daemon.StopAsync(true,token);
            if(!await service.StopAsync(TimeSpan.FromSeconds(10),token))
                logger.LogWarning("Private Ollama installation has not exited within the shutdown wait; files and cancellation state are retained.");
        }
    }

    public static void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<OllamaInstallationService>();
        services.AddSingleton<OllamaManagedDaemon>();
        services.AddHostedService<InstallationLifetime>();
    }

    public static void Map(WebApplication app,LiveSettings settings,Action<string?> persistLocation)
    {
        var installationService=app.Services.GetRequiredService<OllamaInstallationService>();
        var daemon=app.Services.GetRequiredService<OllamaManagedDaemon>();
        var location=new OllamaRuntimeLocation(settings);
        app.MapPost("/api/ollama/runtime/location",(LocationRequest request)=>
        {
            var (code,outcome)=installationService.ChangeLocation(request.Confirmed,request.BaseDirectory,location.EffectiveBase,persistLocation,request.ExpectedPrivateRoot??"");
            return Results.Json(new{outcome},statusCode:code);
        });
        app.MapPost("/api/ollama/runtime/start",async (ActionRequest request,CancellationToken token)=>
        { var result=await daemon.StartAsync(location.EffectiveBase,request.Confirmed,token,request.ExpectedPrivateRoot??""); return Results.Json(result,statusCode:result.StatusCode); });
        app.MapPost("/api/ollama/runtime/stop",async (ActionRequest request,CancellationToken token)=>
        { var result=await daemon.StopAsync(request.Confirmed,token); return Results.Json(result,statusCode:result.StatusCode); });
        app.MapPost("/api/ollama/runtime/select",async (SelectRequest request,CancellationToken token)=>
        { var result=await daemon.SelectAsync(request.Confirmed,request.Target,token); return Results.Json(result,statusCode:result.StatusCode); });
        app.MapPost("/api/ollama/runtime/uninstall",async (InstallRequest request,CancellationToken token)=>
        { var result=await daemon.UninstallAsync(location.EffectiveBase,request.Confirmed,request.ExpectedVersion,token,request.ExpectedPrivateRoot??""); return Results.Json(result,statusCode:result.StatusCode); });
        app.MapPost("/api/ollama/runtime/install",async (InstallRequest request,CancellationToken token)=>
        {
            token.ThrowIfCancellationRequested();
            var result=await installationService.StartAsync(location.EffectiveBase,request.Confirmed,request.ExpectedVersion,request.ExpectedPrivateRoot??"");
            return Results.Json(result,statusCode:result.StatusCode);
        });
        app.MapPost("/api/ollama/runtime/install/cancel",()=>
        {
            var result=installationService.RequestCancellation();
            return Results.Json(result,statusCode:result.StatusCode);
        });
        app.MapGet("/api/ollama/runtime",async (CancellationToken token)=>
        {
            var runtimeBaseDirectory=settings.OllamaRuntimeBaseDirectory;
            var defaultBaseDirectory=location.DefaultBaseDirectory;
            var storageRoot=runtimeBaseDirectory??defaultBaseDirectory;
            token.ThrowIfCancellationRequested();
            string? privateRoot=null;
            string? programDirectory=null;
            string? modelsDirectory=null;
            OllamaManagedFileIdentity managed;
            try
            {
                privateRoot=OllamaInstallationService.ResolvePrivateRoot(storageRoot);
                programDirectory=Path.Combine(privateRoot,OllamaManagedInstallation.ProgramDirectoryName);
                modelsDirectory=Path.Combine(privateRoot,OllamaManagedInstallation.ModelsDirectoryName);
                managed=await OllamaManagedInstallation.InspectAsync(privateRoot,token);
            }
            catch(Exception ex) when(ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                managed=new(OllamaManagedInstallation.ManagedInvalid,"managed-inspection-failed",null,null,null,Array.Empty<string>());
            }

            OllamaRuntimeStatus? connection=null;
            string? connectionError=null;
            try
            {
                connection=await OllamaRuntime.StatusAsync(token);
            }
            catch(Exception ex) when(ex is ArgumentException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Net.Http.HttpRequestException)
            {
                connectionError="connection-inspection-failed";
            }
            token.ThrowIfCancellationRequested();

            return Results.Ok(new
            {
                connection,
                connectionKnown=connectionError is null,
                connectionError,
                managed,
                installation=installationService.Snapshot(),
                paths=new {privateRoot,programDirectory,modelsDirectory,runtimeBaseDirectory,defaultBaseDirectory},
                package=new
                {
                    version=OllamaManagedInstallation.SupportedVersion,
                    url=OllamaManagedInstallation.SupportedPackageUrl,
                    size=OllamaManagedInstallation.SupportedPackageSize,
                    sha256=OllamaManagedInstallation.SupportedPackageSha256
                },
                managementActionsImplemented=true,
                installationActionsImplemented=true,
                daemon=daemon.Snapshot(),
                daemonActionsImplemented=true,
                daemonOwnershipVerified=daemon.Snapshot().Owned,
                recursiveDeleteAllowed=false
            });
        });
    }
}
