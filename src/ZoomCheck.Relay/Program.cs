using ZoomCheck.Relay;

var app = RelayApp.Build(WebApplication.CreateBuilder(args));
await app.RunAsync();
