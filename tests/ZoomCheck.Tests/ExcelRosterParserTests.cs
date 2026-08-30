using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Roster;

namespace ZoomCheck.Tests;

public sealed class ExcelRosterParserTests : IDisposable
{
    private readonly string _workDirectory =
        Path.Combine(Path.GetTempPath(), "zoomcheck-tests-" + Guid.NewGuid().ToString("N"));

    private readonly ExcelRosterParser _parser = new();

    public void Dispose()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }
    }

    private string WriteRoster(string fileName, IReadOnlyList<string?[]> rows, string sheetName = "명단")
        => MinimalXlsxBuilder.WriteWorkbook(_workDirectory, fileName, sheetName, rows);

    private static string?[] Header() => new string?[] { "번호", "성명", "이메일", "연락처", "소속기관" };

    private static string?[] HeaderWith(params string[] extraColumns)
        => Header().Concat(extraColumns.Select(column => (string?)column)).ToArray();

    [Fact]
    public void Parse_ReadsPeopleAndNormalizesFields()
    {
        var path = WriteRoster("roster.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", " 김 영 인 ", " YoungIn@Example.COM ", "010-1234-5678", " 서울대학교 " },
            new string?[] { "2", "이순신", "sunshin@example.com", "01098765432", "해군" }
        });

        var result = _parser.Parse(path);

        Assert.Equal(2, result.People.Count);
        Assert.Equal(Path.GetFullPath(path), result.SourcePath);
        Assert.Equal("roster.xlsx", result.DisplayName);
        Assert.NotEmpty(result.ImportId);

        var first = result.People[0];
        Assert.Equal("1", first.Sequence);
        Assert.Equal("김 영 인", first.Name);
        Assert.Equal("김영인", first.NormalizedName);
        Assert.Equal("youngin@example.com", first.Email);
        Assert.Equal("01012345678", first.Phone);
        Assert.Equal("서울대학교", first.Organization);
    }

    [Fact]
    public void Parse_BuildsAliasesFromNameEmailPhoneAndOrganization()
    {
        var path = WriteRoster("aliases.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김영인", "youngin@example.com", "010-1234-5678", "서울대학교" }
        });

        var person = Assert.Single(_parser.Parse(path).People);

        Assert.Contains("김영인", person.Aliases);
        Assert.Contains("youngin", person.Aliases);
        Assert.Contains("01012345678", person.Aliases);
        Assert.Contains(NameNormalizer.Normalize("김영인서울대학교"), person.Aliases);
    }

    [Fact]
    public void Parse_SkipsRowsWithoutName()
    {
        var path = WriteRoster("gaps.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김영인", "youngin@example.com", "", "" },
            new string?[] { "2", "   ", "blank@example.com", "", "" },
            new string?[] { "3", null, null, null, null },
            new string?[] { "4", "이순신", "", "", "" }
        });

        var result = _parser.Parse(path);

        Assert.Equal(new[] { "김영인", "이순신" }, result.People.Select(person => person.Name));
    }

    [Fact]
    public void Parse_FindsHeaderRowBelowTitleRows()
    {
        var path = WriteRoster("titled.xlsx", new List<string?[]>
        {
            new string?[] { "2026년 연수 참석자 명단" },
            new string?[] { },
            Header(),
            new string?[] { "1", "김영인", "youngin@example.com", "", "" }
        });

        var person = Assert.Single(_parser.Parse(path).People);
        Assert.Equal("김영인", person.Name);
    }

    [Fact]
    public void Parse_ToleratesReorderedAndUnknownColumns()
    {
        var path = WriteRoster("reordered.xlsx", new List<string?[]>
        {
            new string?[] { "성명", "비고", "번호", "소속기관", "이메일" },
            new string?[] { "김영인", "메모", "7", "서울대학교", "youngin@example.com" }
        });

        var person = Assert.Single(_parser.Parse(path).People);

        Assert.Equal("7", person.Sequence);
        Assert.Equal("김영인", person.Name);
        Assert.Equal("서울대학교", person.Organization);
        Assert.Equal("youngin@example.com", person.Email);
        Assert.Equal(string.Empty, person.Phone);
    }

    [Fact]
    public void Parse_AssignsUniqueIdsPerPerson()
    {
        var path = WriteRoster("dupes.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김영인", "", "", "" },
            new string?[] { "2", "김영인", "", "", "" }
        });

        var result = _parser.Parse(path);

        Assert.Equal(2, result.People.Count);
        Assert.Equal(2, result.People.Select(person => person.Id).Distinct().Count());
    }

    [Fact]
    public void Parse_AssignsStableIdsAcrossReimports()
    {
        var path = WriteRoster("stable.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김영인", "youngin@example.com", "", "" }
        });

        var firstId = Assert.Single(_parser.Parse(path).People).Id;
        var secondId = Assert.Single(_parser.Parse(path).People).Id;

        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public void Parse_Throws_WhenNoRosterHeadersPresent()
    {
        var path = WriteRoster("not-a-roster.xlsx", new List<string?[]>
        {
            new string?[] { "foo", "bar" },
            new string?[] { "1", "2" }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => _parser.Parse(path));
        Assert.Contains("성명", exception.Message);
    }

    [Fact]
    public void Parse_Throws_WhenFileIsMissing()
    {
        var missing = Path.Combine(_workDirectory, "missing.xlsx");
        Assert.ThrowsAny<IOException>(() => _parser.Parse(missing));
    }

    [Fact]
    public void Parse_OutputFeedsMatcher_ForEndToEndNameMatching()
    {
        var path = WriteRoster("matching.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김 영 인", "youngin@example.com", "010-1234-5678", "서울대학교" }
        });

        var roster = _parser.Parse(path).People;
        var matcher = new AttendanceMatcher();

        var byName = matcher.Match(roster, new Dictionary<string, string>(), "김영인", null);
        var byEmail = matcher.Match(roster, new Dictionary<string, string>(), "unknown", "YOUNGIN@EXAMPLE.COM");

        Assert.Equal(Core.Enums.MatchConfidence.NameOnly, byName.Confidence);
        Assert.Equal(Core.Enums.MatchConfidence.Verified, byEmail.Confidence);
        Assert.Equal(byName.Person!.Id, byEmail.Person!.Id);
    }

    [Fact]
    public void Parse_LeavesGroupEmpty_WhenRosterHasNoGroupColumn()
    {
        var path = WriteRoster("no-group.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김영인", "", "", "" }
        });

        Assert.Equal(string.Empty, Assert.Single(_parser.Parse(path).People).Group);
    }

    [Theory]
    [InlineData("조")]
    [InlineData("분반")]
    [InlineData("그룹")]
    [InlineData("팀")]
    [InlineData("반")]
    public void Parse_ReadsGroup_FromEachSupportedHeader(string groupHeader)
    {
        var path = WriteRoster($"group-{groupHeader}.xlsx", new List<string?[]>
        {
            HeaderWith(groupHeader),
            new string?[] { "1", "김영인", "", "", "", "2조" }
        });

        Assert.Equal("2조", Assert.Single(_parser.Parse(path).People).Group);
    }

    [Fact]
    public void Parse_PrefersHigherPriorityGroupHeader_WhenSeveralArePresent()
    {
        var path = WriteRoster("group-priority.xlsx", new List<string?[]>
        {
            HeaderWith("반", "팀", "그룹", "분반", "조"),
            new string?[] { "1", "김영인", "", "", "", "반값", "팀값", "그룹값", "분반값", "조값" }
        });

        Assert.Equal("조값", Assert.Single(_parser.Parse(path).People).Group);
    }

    [Fact]
    public void Parse_UsesNextPriorityGroupHeader_WhenEarlierColumnIsBlankForRow()
    {
        var path = WriteRoster("group-priority-blank.xlsx", new List<string?[]>
        {
            HeaderWith("조", "분반"),
            new string?[] { "1", "김영인", "", "", "", "", "오후반" }
        });

        Assert.Equal("오후반", Assert.Single(_parser.Parse(path).People).Group);
    }

    [Fact]
    public void Parse_PrefersBunbanOverBan_BecauseHeaderMatchingIsExact()
    {
        var path = WriteRoster("group-bunban.xlsx", new List<string?[]>
        {
            HeaderWith("반", "분반"),
            new string?[] { "1", "김영인", "", "", "", "반값", "분반값" }
        });

        Assert.Equal("분반값", Assert.Single(_parser.Parse(path).People).Group);
    }

    [Fact]
    public void Parse_TrimsAndCollapsesGroupWhitespace()
    {
        var path = WriteRoster("group-whitespace.xlsx", new List<string?[]>
        {
            HeaderWith("조"),
            new string?[] { "1", "김영인", "", "", "", "  1   조  " },
            new string?[] { "2", "이순신", "", "", "", "\t" },
            new string?[] { "3", "홍길동", "", "", "", "1 조" }
        });

        var people = _parser.Parse(path).People;

        Assert.Equal("1 조", people[0].Group);
        Assert.Equal(string.Empty, people[1].Group);
        // Inconsistent spacing must not split one group into two look-alikes.
        Assert.Equal(people[0].Group, people[2].Group);
    }

    [Fact]
    public void Parse_KeepsPersonIdStable_WhenOnlyGroupChanges()
    {
        var ungrouped = WriteRoster("id-ungrouped.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "김영인", "youngin@example.com", "", "" }
        });
        var grouped = WriteRoster("id-grouped.xlsx", new List<string?[]>
        {
            HeaderWith("조"),
            new string?[] { "1", "김영인", "youngin@example.com", "", "", "3조" }
        });

        var withoutGroup = Assert.Single(_parser.Parse(ungrouped).People);
        var withGroup = Assert.Single(_parser.Parse(grouped).People);

        Assert.Equal(string.Empty, withoutGroup.Group);
        Assert.Equal("3조", withGroup.Group);
        Assert.Equal(withoutGroup.Id, withGroup.Id);
    }

    [Fact]
    public void Parse_ExcludesGroupFromAliases_SoMatchingIsUnaffected()
    {
        var path = WriteRoster("group-aliases.xlsx", new List<string?[]>
        {
            HeaderWith("조"),
            new string?[] { "1", "김영인", "", "", "", "3조" }
        });

        var person = Assert.Single(_parser.Parse(path).People);

        Assert.DoesNotContain("3조", person.Aliases);
        Assert.DoesNotContain(person.Aliases, alias => alias.Contains("3조", StringComparison.Ordinal));
    }
}
