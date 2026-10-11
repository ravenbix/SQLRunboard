using SqlRunboard.Core;

/// <summary>The script reader and GO splitter: file text in, parts with repeat count and start line out.</summary>
public class SqlScriptTests
{
    static ScriptPart[] Split(params string[] lines) => [.. SqlScript.Split(string.Join("\n", lines))];

    [Fact]
    public void Text_with_no_GO_is_one_part_that_starts_on_line_1()
    {
        Assert.Equal([new ScriptPart("select 1\nselect 2", 1, 1)], Split("select 1", "select 2"));
    }

    [Fact]
    public void GO_and_GO_n_alone_on_a_line_split_in_any_case_with_spaces_and_a_trailing_comment()
    {
        Assert.Equal(
            [new ScriptPart("create table t (n int)", 1, 1), new ScriptPart("insert t values (1)", 3, 3), new ScriptPart("select * from t", 1, 5)],
            Split("create table t (n int)", "GO", "insert t values (1)", "  go 3   -- three rows", "select * from t", "Go\t", ""));
    }

    [Fact]
    public void GO_with_other_text_on_its_line_does_not_split()
    {
        string[] lines = ["select 1 GO", "GO select 2", "GO;", "GOTO done", "GO 2 3", "GO -1", "-- GO", "done:"];
        Assert.Equal([new ScriptPart(string.Join("\n", lines), 1, 1)], Split(lines));
    }

    [Fact]
    public void GO_inside_a_block_comment_or_a_string_that_continues_across_lines_does_not_split()
    {
        string[] first =
        [
            "/* outer /* nested */", "GO", "still a comment */",
            "select 'it''s", "GO", "still a string', N'x', [col'umn], \"q'\"",
            "-- a line comment with /* and ' does not open anything",
            "select '--', '/*' /* closed */ -- end",
        ];
        Assert.Equal(
            [new ScriptPart(string.Join("\n", first), 1, 1), new ScriptPart("select 2", 1, 10)],
            Split([.. first, "GO", "select 2"]));
    }

    [Fact]
    public void Blank_parts_are_dropped_and_a_part_keeps_its_leading_blank_lines_so_server_lines_map_to_file_lines()
    {
        Assert.Equal([new ScriptPart("\r\n\r\nselect 1", 5, 2)],[.. SqlScript.Split("GO\r\n\r\n\r\nselect 1\r\nGO 5\r\n  \r\nGO\r\n")]);
    }
}
