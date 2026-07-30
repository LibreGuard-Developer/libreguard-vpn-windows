using LibreGuard.VpnService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LibreGuard VPN Service";
});

builder.Services.AddHostedService<VpnServiceWorker>();
builder.Services.AddSingleton<VpnCommandHandler>();
builder.Services.AddSingleton<OpenVpnProcessManager>();

var host = builder.Build();
host.Run();
