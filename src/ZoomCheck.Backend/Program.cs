using ZoomCheck.Backend;
using ZoomCheck.Infrastructure.Persistence;

var app = BackendApp.Build(WebApplication.CreateBuilder(args));
await app.Services.GetRequiredService<SqliteAttendanceRepository>().InitializeAsync();
await app.RunAsync();
