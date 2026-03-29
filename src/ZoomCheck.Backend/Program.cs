using System.Text.Json.Serialization;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Options;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<ZoomOptions>(builder.Configuration.GetSection(ZoomOptions.SectionName));
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

builder.Services.AddSingleton(new SqliteAttendanceRepository(storageOptions.DatabasePath));
builder.Services.AddSingleton<ExcelRosterParser>();
builder.Services.AddSingleton<AttendanceMatcher>();
builder.Services.AddSingleton<AttendanceApplicationService>();
builder.Services.AddSingleton<ZoomWebhookValidator>();
builder.Services.AddHttpClient<ZoomOAuthTokenService>();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

await app.Services.GetRequiredService<SqliteAttendanceRepository>().InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
