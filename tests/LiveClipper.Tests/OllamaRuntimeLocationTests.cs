using LiveClipper.App;
using LiveClipper.Core;

namespace LiveClipper.Tests;

public sealed class OllamaRuntimeLocationTests : IDisposable
{
    readonly string root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"lc-location-"+Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose()=>Directory.Delete(root,true);

    [Fact] public async Task SelectionPersistsWithoutMovingOldFilesOrCreatingNewOnesAndResetRestoresDefault()
    {
        var config=Path.Combine(root,"settings.json");
        var settings=new LiveSettings{StorageRoot=Path.Combine(root,"legacy")};
        var old=OllamaInstallationService.ResolvePrivateRoot(settings.StorageRoot);
        Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"keep"),"untouched");
        var next=Path.Combine(root,"new-base");
        var service=new OllamaInstallationService();
        var location=new OllamaRuntimeLocation(settings);
        string Effective()=>location.EffectiveBase();
        void Save(string? path)=>StartupDurability.UpdateSettings(config,settings,()=>{settings.OllamaRuntimeBaseDirectory=path;return true;});
        Assert.Equal(settings.StorageRoot,Effective());
        Assert.Equal(400,service.ChangeLocation(false,next,Effective,Save).Code);
        Assert.Equal(400,service.ChangeLocation(true,"relative",Effective,Save).Code);
        Assert.Equal(400,service.ChangeLocation(true,Path.GetPathRoot(root),Effective,Save).Code);
        Assert.False(File.Exists(config));
        Assert.Equal((200,"location-saved"),service.ChangeLocation(true,next,Effective,Save));
        var reopened=StartupDurability.LoadSettings(config,()=>new()).Settings;
        Assert.Equal(settings.StorageRoot,reopened.StorageRoot);
        var loadedLocation=new OllamaRuntimeLocation(reopened);
        Assert.Equal(next,loadedLocation.EffectiveBase());
        Assert.False(Directory.Exists(next));Assert.Equal("untouched",File.ReadAllText(Path.Combine(old,"keep")));
        // An intentionally unowned program directory forces inspection to refuse before download or exec.
        var target=OllamaInstallationService.ResolvePrivateRoot(next);
        Directory.CreateDirectory(Path.Combine(target,"program"));
        string Base()=>loadedLocation.EffectiveBase();
        var daemon=new OllamaManagedDaemon(service);
        Assert.Equal("managed-program-not-valid",(await daemon.StartAsync(Base,true,default)).Outcome);
        Assert.Equal("managed-removal-plan-invalid",(await daemon.UninstallAsync(Base,true,OllamaManagedInstallation.SupportedVersion,default)).Outcome);
        var refused=await service.StartAsync(Base,true,OllamaManagedInstallation.SupportedVersion);
        Assert.Equal(409,refused.StatusCode);Assert.Equal(target,service.Snapshot().PrivateRoot);
        Assert.True(SpinWait.SpinUntil(()=>{using var l=service.TryAcquireManagement();return l is not null;},TimeSpan.FromSeconds(2)));
        Assert.Equal(200,service.ChangeLocation(true,next,Effective,Save,target).Code);
        Assert.Equal("existing-program-not-managed-valid",service.Snapshot().Reason);
        var empty=Path.Combine(root,"empty-base");
        Assert.Equal(500,service.ChangeLocation(true,empty,Effective,_=>throw new IOException("disk unavailable"),target).Code);
        Assert.Equal(target,service.Snapshot().PrivateRoot);
        Assert.Equal(200,service.ChangeLocation(true,empty,Effective,Save,target).Code);
        Assert.Equal("idle",service.Snapshot().State);Assert.Null(service.Snapshot().Reason);
        Assert.Equal(OllamaManagedInstallation.NotInstalled,(await OllamaManagedInstallation.InspectAsync(OllamaInstallationService.ResolvePrivateRoot(Effective()))).Status);
        foreach(var stale in new[]{target,""})
        {
            Assert.Equal("private-root-changed",(await service.StartAsync(Effective,true,OllamaManagedInstallation.SupportedVersion,stale)).Outcome);
            Assert.Equal("private-root-changed",(await daemon.StartAsync(Effective,true,default,stale)).Outcome);
            Assert.Equal("private-root-changed",(await daemon.UninstallAsync(Effective,true,OllamaManagedInstallation.SupportedVersion,default,stale)).Outcome);
            Assert.Equal("private-root-changed",service.ChangeLocation(true,null,Effective,Save,stale).Outcome);
        }
        Assert.False(Directory.Exists(empty));
        Assert.Equal(200,service.ChangeLocation(true,null,Effective,Save).Code);
        Assert.Null(StartupDurability.LoadSettings(config,()=>new()).Settings.OllamaRuntimeBaseDirectory);
        Assert.Equal(settings.StorageRoot,Effective());
        Assert.True(Directory.Exists(Path.Combine(target,"program")));Assert.True(File.Exists(Path.Combine(old,"keep")));
    }

    [Fact] public void GeneralSettingsRootChangeAppliesAtRestartNotUnderOwnedLease()
    {
        var legacy=Path.Combine(root,"legacy");var future=Path.Combine(root,"future");
        var settings=new LiveSettings{StorageRoot=legacy};
        var live=new OllamaRuntimeLocation(settings);var service=new OllamaInstallationService();
        using(var owned=service.TryAcquireManagement())
        {
            Assert.NotNull(owned);settings.StorageRoot=future;
            Assert.Equal(legacy,live.DefaultBaseDirectory);Assert.Equal(legacy,live.EffectiveBase());
            Assert.Equal(409,service.ChangeLocation(true,future,live.EffectiveBase,_=>throw new InvalidOperationException()).Code);
        }
        var custom=Path.Combine(root,"custom");
        Assert.Equal(200,service.ChangeLocation(true,custom,live.EffectiveBase,p=>settings.OllamaRuntimeBaseDirectory=p).Code);
        Assert.Equal(custom,live.EffectiveBase());Assert.Equal(custom,new OllamaRuntimeLocation(settings).EffectiveBase());
        Assert.Equal(200,service.ChangeLocation(true,null,live.EffectiveBase,p=>settings.OllamaRuntimeBaseDirectory=p).Code);
        Assert.Equal(legacy,live.EffectiveBase());Assert.Equal(future,new OllamaRuntimeLocation(settings).EffectiveBase());
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact] public async Task LocationAdmissionAndLifecycleAdmissionAreMutuallyExclusive()
    {
        var service=new OllamaInstallationService();var daemon=new OllamaManagedDaemon(service);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save=Task.Run(()=>service.ChangeLocation(true,root,()=>root,_=>{entered.SetResult();release.Task.GetAwaiter().GetResult();}));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string Unexpected()=>throw new InvalidOperationException("read before lease");
        try
        {
            Assert.Equal(409,(await service.StartAsync(Unexpected,true,OllamaManagedInstallation.SupportedVersion)).StatusCode);
            Assert.Equal(409,(await daemon.StartAsync(Unexpected,true,default)).StatusCode);
            Assert.Equal(409,(await daemon.UninstallAsync(Unexpected,true,OllamaManagedInstallation.SupportedVersion,default)).StatusCode);
        }
        finally {release.SetResult();}
        Assert.Equal(200,(await save).Code);
        using var lease=service.TryAcquireManagement();Assert.NotNull(lease);
        Assert.Equal(409,service.ChangeLocation(true,null,()=>root,_=>throw new InvalidOperationException()).Code);
    }
}
