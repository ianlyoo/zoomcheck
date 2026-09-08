using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class RosterImportIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reimport_ReturnsPersistedPersonIds(bool useStream)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"zoomcheck-import-{Guid.NewGuid():N}");
        try
        {
            var file = MinimalXlsxBuilder.WriteWorkbook(directory, "roster.xlsx", "명단", new[]
            {
                new[] { "번호", "성명", "이메일", "조" },
                new[] { "1", "김민수", "minsu@example.com", "1조" }
            });
            var repository = new SqliteAttendanceRepository(Path.Combine(directory, "attendance.db"));
            await repository.InitializeAsync();
            var service = new AttendanceApplicationService(new ExcelRosterParser(), repository, new AttendanceMatcher());
            var original = await service.ImportRosterAsync(file);

            MinimalXlsxBuilder.WriteWorkbook(directory, "roster.xlsx", "명단", new[]
            {
                new[] { "번호", "성명", "이메일", "조" },
                new[] { "2", "김민수", "minsu@example.com", "1조" }
            });

            using var stream = File.OpenRead(file);
            var result = useStream
                ? await service.ImportRosterAsync(stream, "roster.xlsx")
                : await service.ImportRosterAsync(file);

            var person = Assert.Single(result.People);
            Assert.Equal(Assert.Single(original.People).Id, person.Id);
            Assert.Equal(Assert.Single(await service.GetRosterAsync()).Id, person.Id);
            Assert.Equal("1조", person.Group);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
