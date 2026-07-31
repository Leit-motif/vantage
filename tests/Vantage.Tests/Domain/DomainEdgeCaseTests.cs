using Vantage.Core.Workflow;
using Vantage.Infrastructure.Parsing;

namespace Vantage.Tests.Domain;

/// <summary>
/// Dense domain edge cases that are quicker to diagnose here than through the refresh seam.
/// These still assert domain inputs and outputs, never how the answer is computed.
/// </summary>
[TestClass]
public sealed class DomainEdgeCaseTests
{
    [TestMethod]
    [DataRow("ready", WorkflowStatus.Ready)]
    [DataRow("Ready", WorkflowStatus.Ready)]
    [DataRow("ready-for-agent", WorkflowStatus.Ready)]
    [DataRow("READY_FOR_AGENT", WorkflowStatus.Ready)]
    [DataRow("in progress", WorkflowStatus.InProgress)]
    [DataRow("in-progress", WorkflowStatus.InProgress)]
    [DataRow("blocked", WorkflowStatus.Blocked)]
    [DataRow("resolved", WorkflowStatus.Resolved)]
    [DataRow("done", WorkflowStatus.Done)]
    [DataRow("open", WorkflowStatus.Open)]
    [DataRow("marinating", WorkflowStatus.Unknown)]
    [DataRow("", WorkflowStatus.Unknown)]
    public void Status_recognition_tolerates_casing_and_separators(string raw, WorkflowStatus expected) =>
        Assert.AreEqual(expected, WorkflowStatusVocabulary.Read(raw).Status);

    [TestMethod]
    [DataRow("DONE ✅", "done")]
    [DataRow("[done]", "done")]
    [DataRow("Complete!", "done")]
    public void Decorated_completion_is_recognized_only_through_a_named_alias(string raw, string alias)
    {
        var reading = WorkflowStatusVocabulary.Read(raw);

        Assert.AreEqual(WorkflowStatus.Done, reading.Status);
        Assert.AreEqual(alias, reading.AliasApplied);
        Assert.AreEqual(raw, reading.RawValue);
    }

    [TestMethod]
    public void An_undecorated_status_needs_no_alias()
    {
        var reading = WorkflowStatusVocabulary.Read("done");

        Assert.AreEqual(WorkflowStatus.Done, reading.Status);
        Assert.IsNull(reading.AliasApplied);
    }

    [TestMethod]
    [DataRow("git@github.com:acme/widget.git", "acme/widget")]
    [DataRow("https://github.com/acme/widget.git", "acme/widget")]
    [DataRow("https://github.com/acme/widget", "acme/widget")]
    [DataRow("ssh://git@github.com/acme/widget.git", "acme/widget")]
    public void A_GitHub_origin_normalizes_to_owner_and_repository(string url, string slug) =>
        Assert.AreEqual(slug, GitHubOrigin.TryParse(url)!.Slug);

    [TestMethod]
    [DataRow("https://gitlab.com/acme/widget.git")]
    [DataRow("")]
    [DataRow("not a url")]
    public void A_non_GitHub_remote_produces_no_origin(string url) =>
        Assert.IsNull(GitHubOrigin.TryParse(url));

    [TestMethod]
    [DataRow("Status: ready", "status", "ready")]
    [DataRow("**Status:** ready", "status", "ready")]
    [DataRow("**Status**: ready", "status", "ready")]
    [DataRow("- Blocked by: 001, 002", "blocked by", "001, 002")]
    [DataRow("- **Blocked by:** 001", "blocked by", "001")]
    public void Metadata_is_read_from_plain_and_bold_syntax(string line, string key, string value)
    {
        Assert.IsTrue(MetadataGrammar.TryReadField(line, 1, out var field));
        Assert.AreEqual(key, field.Key);
        Assert.AreEqual(value, field.RawValue);
    }

    [TestMethod]
    public void Metadata_inside_a_fenced_block_is_content_not_configuration()
    {
        var (_, fields) = MetadataGrammar.Parse("""
            # Ticket

            Status: ready

            ```
            Status: done
            ```
            """);

        Assert.AreEqual(1, fields.Count(f => f.Key == "status"));
        Assert.AreEqual("ready", fields.Single(f => f.Key == "status").RawValue);
    }

    [TestMethod]
    public void Front_matter_metadata_is_read_the_same_way_as_body_metadata()
    {
        var (_, fields) = MetadataGrammar.Parse("---\nstatus: blocked\n---\n\n# Ticket\n");

        Assert.AreEqual("blocked", fields.Single(f => f.Key == "status").RawValue);
    }

    [TestMethod]
    public void Semantic_hashing_ignores_line_endings_and_byte_order_marks_only()
    {
        var unix = "# Ticket\nStatus: ready\n";
        var windows = "﻿# Ticket\r\nStatus: ready\r\n";

        Assert.AreEqual(SemanticHash.Compute(unix), SemanticHash.Compute(windows));
        Assert.AreNotEqual(
            SemanticHash.Compute(unix),
            SemanticHash.Compute("# Ticket\nStatus: done\n"),
            "A substantive change must always change the hash.");
        Assert.AreNotEqual(
            SemanticHash.Compute(unix),
            SemanticHash.Compute("# Ticket\nStatus:  ready\n"),
            "Whitespace inside a line is content, not formatting to normalize away.");
    }
}
