using System;
using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PersonalBATranscriber.Core.Models;

namespace PersonalBATranscriber.Core.Services;

public class DocxExportService
{
    public void ExportToWord(
        string targetFilePath,
        ProjectMetadata project,
        IEnumerable<CleanSentence> sentences)
    {
        var dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var wordDoc = WordprocessingDocument.Create(targetFilePath, WordprocessingDocumentType.Document);
        var mainPart = wordDoc.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = mainPart.Document.AppendChild(new Body());

        // Page setup: Standard A4
        var sectionProps = new SectionProperties();
        var pageSize = new PageSize() { Width = 11906U, Height = 16838U }; // A4 in dxa
        var pageMargin = new PageMargin() { Top = 1440, Right = 1440, Bottom = 1440, Left = 1440 }; // 1 inch
        sectionProps.Append(pageSize, pageMargin);

        // Document Title
        var titlePara = body.AppendChild(new Paragraph());
        var titleRun = titlePara.AppendChild(new Run());
        titleRun.AppendChild(new Text(project.ProjectName));
        titleRun.RunProperties = new RunProperties(
            new Bold(),
            new FontSize() { Val = "36" }, // 18pt
            new Color() { Val = "1A365D" }, // Deep Navy
            new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri" }
        );
        titlePara.ParagraphProperties = new ParagraphProperties(
            new SpacingBetweenLines() { After = "240" }
        );

        // Metadata Table
        var table = body.AppendChild(new Table());
        var tblPr = new TableProperties(
            new TableBorders(
                new TopBorder() { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new BottomBorder() { Val = BorderValues.Single, Size = 4, Color = "D1D5DB" },
                new LeftBorder() { Val = BorderValues.None },
                new RightBorder() { Val = BorderValues.None },
                new InsideHorizontalBorder() { Val = BorderValues.Single, Size = 2, Color = "E5E7EB" },
                new InsideVerticalBorder() { Val = BorderValues.None }
            ),
            new TableWidth() { Width = "5000", Type = TableWidthUnitValues.Pct }
        );
        table.AppendChild(tblPr);

        AddMetaRow(table, "Source File", Path.GetFileName(project.SourceMediaFilePath));
        AddMetaRow(table, "Recording Date", project.CreatedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        AddMetaRow(table, "Audio Duration", TimeSpan.FromSeconds(project.MediaDurationSeconds).ToString(@"hh\:mm\:ss"));
        AddMetaRow(table, "Transcript Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

        // Spacer
        var spacer = body.AppendChild(new Paragraph());
        spacer.ParagraphProperties = new ParagraphProperties(new SpacingBetweenLines() { After = "360" });

        // Content Section Header
        var h2 = body.AppendChild(new Paragraph());
        var h2Run = h2.AppendChild(new Run());
        h2Run.AppendChild(new Text("Meeting Discussion & Action Log"));
        h2Run.RunProperties = new RunProperties(
            new Bold(),
            new FontSize() { Val = "28" }, // 14pt
            new Color() { Val = "2B6CB0" },
            new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri" }
        );
        h2.ParagraphProperties = new ParagraphProperties(new SpacingBetweenLines() { After = "200" });

        // Transcript Sentences
        foreach (var sentence in sentences)
        {
            var p = body.AppendChild(new Paragraph());
            p.ParagraphProperties = new ParagraphProperties(
                new SpacingBetweenLines() { Line = "276", LineRule = LineSpacingRuleValues.Auto, After = "120" } // 1.15 line spacing, 6pt after
            );

            // Timestamp Tag: [00:14:15]
            var timeRun = p.AppendChild(new Run());
            timeRun.AppendChild(new Text($"[{sentence.FormattedTimestamp}] "));
            timeRun.RunProperties = new RunProperties(
                new Bold(),
                new FontSize() { Val = "18" }, // 9pt
                new Color() { Val = "718096" }, // Slate Gray
                new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri" }
            );

            // Speaker Tag
            var spkRun = p.AppendChild(new Run());
            spkRun.AppendChild(new Text($"{sentence.SpeakerLabel}: "));
            spkRun.RunProperties = new RunProperties(
                new Bold(),
                new FontSize() { Val = "22" }, // 11pt
                new Color() { Val = "2D3748" },
                new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri" }
            );

            // Sentence Text with Unresolved marker highlighting
            var text = sentence.DisplayText;
            var textRun = p.AppendChild(new Run());
            textRun.AppendChild(new Text(text));
            
            var textRunPr = new RunProperties(
                new FontSize() { Val = "22" }, // 11pt
                new Color() { Val = "1A202C" },
                new RunFonts() { Ascii = "Calibri", HighAnsi = "Calibri" }
            );

            if (text.Contains("[Inaudible", StringComparison.OrdinalIgnoreCase) || 
                text.Contains("[Unclear", StringComparison.OrdinalIgnoreCase))
            {
                textRunPr.Append(new Color() { Val = "DD6B20" }); // Bold Amber/Orange
                textRunPr.Append(new Bold());
            }

            textRun.RunProperties = textRunPr;
        }

        body.AppendChild(sectionProps);
        wordDoc.Save();
    }

    private static void AddMetaRow(Table table, string label, string value)
    {
        var row = table.AppendChild(new TableRow());

        var cell1 = row.AppendChild(new TableCell());
        cell1.AppendChild(new Paragraph(new Run(new Text(label))
        {
            RunProperties = new RunProperties(
                new Bold(),
                new FontSize() { Val = "18" },
                new Color() { Val = "4A5568" },
                new RunFonts() { Ascii = "Calibri" })
        }));

        var cell2 = row.AppendChild(new TableCell());
        cell2.AppendChild(new Paragraph(new Run(new Text(value))
        {
            RunProperties = new RunProperties(
                new FontSize() { Val = "18" },
                new Color() { Val = "2D3748" },
                new RunFonts() { Ascii = "Calibri" })
        }));
    }
}
