using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PersonalBATranscriber.Core.Models;
using PersonalBATranscriber.Core.Services;
using Xunit;

namespace PersonalBATranscriber.Tests;

public class PersonalBATranscriberUnitTests
{
    [Fact]
    public async Task CostLedger_Enforces_Budget_Ceiling_At_20_USD()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"test_ledger_{Guid.NewGuid():N}.db");
        try
        {
            var ledger = new CostLedgerService(tempDb);

            // Test 1: Initial state has 0 spend
            var (committed, reserved, hours) = await ledger.GetMonthlyUsageAsync();
            Assert.Equal(0m, committed);
            Assert.Equal(0m, reserved);
            Assert.True(await ledger.CanAffordAsync(5.00m));

            // Test 2: Reserve $15.00
            var tx1 = await ledger.ReserveSpendAsync("proj-1", "GROQ", "whisper-large-v3", 3600, 15.00m);
            Assert.NotEmpty(tx1);

            // Test 3: Can afford $4.00 ($15 + $4 = $19 <= $20)
            Assert.True(await ledger.CanAffordAsync(4.00m));

            // Test 4: Cannot afford $6.00 ($15 + $6 = $21 > $20)
            Assert.False(await ledger.CanAffordAsync(6.00m));

            // Test 5: Attempting to reserve $6.00 throws InvalidOperationException
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await ledger.ReserveSpendAsync("proj-2", "GROQ", "whisper-large-v3", 1800, 6.00m);
            });

            // Test 6: Commit tx1 with actual cost $14.50
            await ledger.CommitSpendAsync(tx1, 14.50m, 1000, 800);

            var (committedAfter, reservedAfter, _) = await ledger.GetMonthlyUsageAsync();
            Assert.Equal(14.50m, committedAfter);
            Assert.Equal(0m, reservedAfter);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    [Fact]
    public void DocxExportService_Creates_Valid_Word_Document()
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"test_export_{Guid.NewGuid():N}.docx");
        try
        {
            var exporter = new DocxExportService();
            var meta = new ProjectMetadata
            {
                ProjectName = "Sprint 42 Architecture Review",
                SourceMediaFilePath = "C:\\Recordings\\Sprint42.mp4",
                MediaDurationSeconds = 3600
            };

            var sentences = new List<CleanSentence>
            {
                new()
                {
                    SentenceId = "SNT-0001",
                    AnchorTimestamp = 15.4,
                    SpeakerLabel = "Speaker 1",
                    CleanedText = "We discussed the Kafka consumer lag and verified the dead-letter queue policy."
                },
                new()
                {
                    SentenceId = "SNT-0002",
                    AnchorTimestamp = 45.8,
                    SpeakerLabel = "Speaker 2",
                    CleanedText = "Regarding deployment, [Unclear: Jenkins or GitHub Actions? ~00:00:46] we will test in UAT first."
                }
            };

            exporter.ExportToWord(tempDocx, meta, sentences);

            Assert.True(File.Exists(tempDocx));
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(tempDocx, false))
            {
                Assert.NotNull(doc.MainDocumentPart);
                Assert.NotNull(doc.MainDocumentPart.Document.Body);
                var text = doc.MainDocumentPart.Document.Body.InnerText;
                Assert.Contains("Sprint 42 Architecture Review", text);
                Assert.Contains("Kafka", text);
            }
        }
        finally
        {
            if (File.Exists(tempDocx)) File.Delete(tempDocx);
        }
    }

    [Fact]
    public async Task ProjectDatabaseService_Saves_And_Loads_Faithfully()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"test_project_{Guid.NewGuid():N}.taproj");
        try
        {
            var meta = new ProjectMetadata
            {
                ProjectName = "Backlog Grooming",
                SourceMediaFilePath = "C:\\test\\meeting.m4a",
                MediaDurationSeconds = 1800
            };

            var segments = new List<AcousticSegment>
            {
                new()
                {
                    SegmentId = "SEG-0001",
                    ChunkIndex = 0,
                    StartTimeSeconds = 0,
                    EndTimeSeconds = 600,
                    RawTranscript = "Raw audio text 1",
                    AvgLogProb = -0.2
                }
            };

            var sentences = new List<CleanSentence>
            {
                new()
                {
                    SentenceId = "SNT-0001",
                    ParentSegmentId = "SEG-0001",
                    AnchorTimestamp = 12.5,
                    SpeakerLabel = "Speaker 1",
                    CleanedText = "Cleaned English text 1",
                    UserEditedText = "User manually edited text 1",
                    DisplayOrder = 1
                }
            };

            await ProjectDatabaseService.SaveProjectAsync(tempDb, meta, segments, sentences);

            var (loadedMeta, loadedSegs, loadedSentences) = await ProjectDatabaseService.LoadProjectAsync(tempDb);

            Assert.Equal("Backlog Grooming", loadedMeta.ProjectName);
            Assert.Single(loadedSegs);
            Assert.Single(loadedSentences);
            Assert.Equal("User manually edited text 1", loadedSentences[0].DisplayText);
            Assert.Equal(12.5, loadedSentences[0].AnchorTimestamp);
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }
}
