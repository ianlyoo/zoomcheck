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

        var exception = Assert.Throws<RosterImportException>(() => _parser.Parse(path));
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Equal(RosterImportErrorCode.MissingHeaders, exception.ErrorCode);
        Assert.Equal("ROSTER_MISSING_HEADERS", exception.ErrorCodeName);
        Assert.Equal("roster_err_missing_headers", exception.MessageKey);
        Assert.Equal(new[] { "foo", "bar" }, exception.FoundHeaders);
        Assert.Equal(new[] { "name", "sequence" }, exception.MissingColumns);
        Assert.Contains("성명", exception.Message);
    }

    [Fact]
    public void Parse_ThrowsNotExcel_WhenFileContainsPlainText()
    {
        Directory.CreateDirectory(_workDirectory);
        var path = Path.Combine(_workDirectory, "not-excel.xlsx");
        File.WriteAllText(path, "this is not an excel workbook");

        var exception = Assert.Throws<RosterImportException>(() => _parser.Parse(path));
        Assert.Equal(RosterImportErrorCode.NotExcel, exception.ErrorCode);
        Assert.Equal("ROSTER_NOT_EXCEL", exception.ErrorCodeName);
        Assert.Equal("roster_err_not_excel", exception.MessageKey);
    }

    [Fact]
    public void Parse_ThrowsEmptySheet_WhenWorkbookHasNoData()
    {
        var path = WriteRoster("empty.xlsx", Array.Empty<string?[]>());

        var exception = Assert.Throws<RosterImportException>(() => _parser.Parse(path));
        Assert.Equal(RosterImportErrorCode.EmptySheet, exception.ErrorCode);
        Assert.Equal("ROSTER_EMPTY_SHEET", exception.ErrorCodeName);
        Assert.Equal("roster_err_empty_sheet", exception.MessageKey);
    }

    [Fact]
    public void Parse_ThrowsNoRosterRows_WhenOnlyHeaderRowPresent()
    {
        var path = WriteRoster("header-only.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "1", "   ", "", "", "" }
        });

        var exception = Assert.Throws<RosterImportException>(() => _parser.Parse(path));
        Assert.Equal(RosterImportErrorCode.NoRosterRows, exception.ErrorCode);
        Assert.Equal("ROSTER_NO_ROWS", exception.ErrorCodeName);
        Assert.Equal("roster_err_no_rows", exception.MessageKey);
        Assert.Equal("명단", exception.SheetName);
    }

    [Fact]
    public void Parse_ReportsMissingSequenceColumn_WhenOnlyNameHeaderIsPresent()
    {
        var path = WriteRoster("missing-sequence.xlsx", new List<string?[]>
        {
            new string?[] { "성명", "이메일" },
            new string?[] { "학생001", "student001@example.com" }
        });

        var exception = Assert.Throws<RosterImportException>(() => _parser.Parse(path));
        Assert.Equal(RosterImportErrorCode.MissingHeaders, exception.ErrorCode);
        Assert.Equal(new[] { "성명", "이메일" }, exception.FoundHeaders);
        Assert.Equal(new[] { "sequence" }, exception.MissingColumns);
    }

    [Fact]
    public void Parse_ReportsMissingNameColumn_WhenOnlySequenceHeaderIsPresent()
    {
        var path = WriteRoster("missing-name.xlsx", new List<string?[]>
        {
            new string?[] { "번호", "이메일" },
            new string?[] { "1", "student001@example.com" }
        });

        var exception = Assert.Throws<RosterImportException>(() => _parser.Parse(path));
        Assert.Equal(RosterImportErrorCode.MissingHeaders, exception.ErrorCode);
        Assert.Equal(new[] { "번호", "이메일" }, exception.FoundHeaders);
        Assert.Equal(new[] { "name" }, exception.MissingColumns);
    }

    [Fact]
    public void Parse_ReportsDuplicateRosterNumbers_WithoutDroppingRows()
    {
        var path = WriteRoster("duplicate-numbers.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "5", "학생005", "student005@example.com", "", "" },
            new string?[] { "6", "학생006", "student006@example.com", "", "" },
            new string?[] { "5", "학생005", "student005@example.com", "", "" },
            new string?[] { "7", "학생007", "", "", "" },
            new string?[] { "7", "학생008", "", "", "" },
            new string?[] { "31", "학생005", "student005b@example.com", "", "" }
        });

        var result = _parser.Parse(path);

        Assert.Equal(6, result.People.Count);
        Assert.NotNull(result.DuplicateNumbers);
        Assert.Equal(2, result.DuplicateNumbers!.Count);

        var dup5 = result.DuplicateNumbers[0];
        Assert.Equal("5", dup5.Sequence);
        Assert.Equal(2, dup5.Count);
        Assert.Equal(new[] { "학생005" }, dup5.Names);
        Assert.Equal(new[] { 2, 4 }, dup5.RowNumbers);

        var dup7 = result.DuplicateNumbers[1];
        Assert.Equal("7", dup7.Sequence);
        Assert.Equal(2, dup7.Count);
        Assert.Equal(new[] { "학생007", "학생008" }, dup7.Names);
        Assert.Equal(new[] { 5, 6 }, dup7.RowNumbers);
    }

    [Fact]
    public void Parse_DoesNotReportBlankSequencesAsDuplicates()
    {
        var path = WriteRoster("blank-sequences.xlsx", new List<string?[]>
        {
            Header(),
            new string?[] { "", "학생001", "", "", "" },
            new string?[] { "  ", "학생002", "", "", "" }
        });

        var result = _parser.Parse(path);

        Assert.Equal(2, result.People.Count);
        Assert.NotNull(result.DuplicateNumbers);
        Assert.Empty(result.DuplicateNumbers!);
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

    [Fact]
    public void Parse_ReadsEnglishHeaders_Successfully()
    {
        var path = WriteRoster("roster-en.xlsx", new List<string?[]>
        {
            new string?[] { "ID", "Name", "Email", "Phone", "Organization", "Group" },
            new string?[] { "101", "John Doe", "john@example.com", "1234567890", "Acme Corp", "Team A" }
        }, "Students");

        var result = _parser.Parse(path);
        var person = Assert.Single(result.People);

        Assert.Equal("101", person.Sequence);
        Assert.Equal("John Doe", person.Name);
        Assert.Equal("john@example.com", person.Email);
        Assert.Equal("1234567890", person.Phone);
        Assert.Equal("Acme Corp", person.Organization);
        Assert.Equal("Team A", person.Group);
    }

    [Fact]
    public void Parse_ReadsSpanishHeaders_Successfully()
    {
        var path = WriteRoster("roster-es.xlsx", new List<string?[]>
        {
            new string?[] { "DNI", "Nombre", "Correo", "Teléfono", "Institución", "Comisión" },
            new string?[] { "202", "Juan Pérez", "juan@ejemplo.com", "011-4321-8765", "Universidad de Buenos Aires", "Comisión 1" }
        }, "Alumnos");

        var result = _parser.Parse(path);
        var person = Assert.Single(result.People);

        Assert.Equal("202", person.Sequence);
        Assert.Equal("Juan Pérez", person.Name);
        Assert.Equal("juan@ejemplo.com", person.Email);
        Assert.Equal("01143218765", person.Phone);
        Assert.Equal("Universidad de Buenos Aires", person.Organization);
        Assert.Equal("Comisión 1", person.Group);
    }

    [Fact]
    public void Parse_PreservesOriginalHeaderAndPersonIdsAcrossRowsAndSheets()
    {
        string?[][] roster =
        [
            ["No", "Name"],
            [" 번호 ", " 성명 ", "Email", "전화번호", "Organization", "Group"],
            ["1", "김하늘", "other@example.test", "5551234", "Other", "Team A"],
            ["1", "김하늘"]
        ];
        var path = MinimalXlsxBuilder.WriteWorkbook(_workDirectory, "multiple.xlsx",
            ("Notes", [["No", "Name"], ["9", "Other Person"]]),
            ("Korean", roster),
            ("Later Korean", [Header(), ["8", "나중사람"]]));
        var original = WriteRoster("original.xlsx", [roster[0], Header(), roster[2][..2], roster[3]]);

        var people = _parser.Parse(path).People;
        Assert.Equal(2, people.Count);
        Assert.Equal(_parser.Parse(original).People.Select(person => person.Id), people.Select(person => person.Id));
        Assert.All(people, person =>
        {
            Assert.Equal("김하늘", person.Name);
            Assert.Equal("1", person.Sequence);
            Assert.Equal(string.Empty, person.Email);
            Assert.Equal(string.Empty, person.Phone);
            Assert.Equal(string.Empty, person.Organization);
            Assert.Equal(string.Empty, person.Group);
        });
    }

    [Fact]
    public void Parse_PrefersSpecificNameAndIdOverGenericAliases()
    {
        var path = WriteRoster("specific.xlsx",
        [
            ["Student ID", "Student Name", "ID", "name", "No", "Number", "Full Name"],
            ["000101", "Alice Student", "42", "Parent A", "1", "555555", "Alice Student Full"]
        ]);

        var person = Assert.Single(_parser.Parse(path).People);
        Assert.Equal("000101", person.Sequence);
        Assert.Equal("Alice Student", person.Name);
    }

    [Fact]
    public void Parse_PrefersNumberOverNoAndFullNameOverName()
    {
        var path = WriteRoster("number.xlsx",
        [
            ["No", "Name", "Number", " Full Name "],
            ["7", "Parent A", "555555", "Alice Student"]
        ]);

        var person = Assert.Single(_parser.Parse(path).People);
        Assert.Equal("555555", person.Sequence);
        Assert.Equal("Alice Student", person.Name);
    }

    [Fact]
    public void Parse_UsesLeftmostDuplicateAfterTrimmingAndIgnoringCase()
    {
        var path = WriteRoster("duplicates.xlsx",
        [
            [" ID ", "id", " nAmE ", "NAME"],
            ["42", "101", "Alice Student", "Parent A"]
        ]);

        var person = Assert.Single(_parser.Parse(path).People);
        Assert.Equal("42", person.Sequence);
        Assert.Equal("Alice Student", person.Name);
    }
}
