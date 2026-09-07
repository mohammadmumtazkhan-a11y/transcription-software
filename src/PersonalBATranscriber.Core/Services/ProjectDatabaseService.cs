using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using PersonalBATranscriber.Core.Models;

namespace PersonalBATranscriber.Core.Services;

public class ProjectDatabaseService
{
    public static void InitializeProjectDatabase(string projectDbPath)
    {
        var dir = Path.GetDirectoryName(projectDbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (!File.Exists(projectDbPath))
        {
            SQLiteConnection.CreateFile(projectDbPath);
        }

        using var conn = new SQLiteConnection($"Data Source={projectDbPath};Version=3;");
        conn.Open();

        var sql = @"
            CREATE TABLE IF NOT EXISTS ProjectMetadata (
                ProjectId TEXT PRIMARY KEY,
                ProjectName TEXT NOT NULL,
                SourceMediaFilePath TEXT NOT NULL,
                ExtractedAudioFilePath TEXT NOT NULL,
                MediaDurationSeconds REAL NOT NULL,
                CreatedDate TEXT NOT NULL,
                LastModifiedDate TEXT NOT NULL,
                Status TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AcousticSegments (
                SegmentId TEXT PRIMARY KEY,
                ChunkIndex INTEGER NOT NULL,
                StartTimeSeconds REAL NOT NULL,
                EndTimeSeconds REAL NOT NULL,
                RawTranscript TEXT NOT NULL,
                AvgLogProb REAL,
                CompressionRatio REAL,
                IsFlaggedForReview INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS CleanSentences (
                SentenceId TEXT PRIMARY KEY,
                ParentSegmentId TEXT NOT NULL,
                AnchorTimestamp REAL NOT NULL,
                SpeakerLabel TEXT DEFAULT 'Speaker 1',
                CleanedText TEXT NOT NULL,
                UserEditedText TEXT,
                DisplayOrder INTEGER NOT NULL
            );
        ";

        using var cmd = new SQLiteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public static async Task SaveProjectAsync(
        string projectDbPath,
        ProjectMetadata meta,
        IEnumerable<AcousticSegment> segments,
        IEnumerable<CleanSentence> sentences)
    {
        InitializeProjectDatabase(projectDbPath);

        using var conn = new SQLiteConnection($"Data Source={projectDbPath};Version=3;");
        await conn.OpenAsync();

        using var tx = conn.BeginTransaction();

        // 1. Update ProjectMetadata
        var metaSql = @"
            INSERT OR REPLACE INTO ProjectMetadata (
                ProjectId, ProjectName, SourceMediaFilePath, ExtractedAudioFilePath,
                MediaDurationSeconds, CreatedDate, LastModifiedDate, Status
            ) VALUES (
                @id, @name, @src, @ext, @dur, @created, @mod, @status
            );
        ";
        using (var cmd = new SQLiteCommand(metaSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", meta.ProjectId);
            cmd.Parameters.AddWithValue("@name", meta.ProjectName);
            cmd.Parameters.AddWithValue("@src", meta.SourceMediaFilePath);
            cmd.Parameters.AddWithValue("@ext", meta.ExtractedAudioFilePath);
            cmd.Parameters.AddWithValue("@dur", meta.MediaDurationSeconds);
            cmd.Parameters.AddWithValue("@created", meta.CreatedDate.ToString("o"));
            cmd.Parameters.AddWithValue("@mod", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@status", meta.Status);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Insert or replace segments
        foreach (var seg in segments)
        {
            var segSql = @"
                INSERT OR REPLACE INTO AcousticSegments (
                    SegmentId, ChunkIndex, StartTimeSeconds, EndTimeSeconds,
                    RawTranscript, AvgLogProb, CompressionRatio, IsFlaggedForReview
                ) VALUES (
                    @id, @chunk, @start, @end, @raw, @logp, @comp, @flag
                );
            ";
            using var cmd = new SQLiteCommand(segSql, conn, tx);
            cmd.Parameters.AddWithValue("@id", seg.SegmentId);
            cmd.Parameters.AddWithValue("@chunk", seg.ChunkIndex);
            cmd.Parameters.AddWithValue("@start", seg.StartTimeSeconds);
            cmd.Parameters.AddWithValue("@end", seg.EndTimeSeconds);
            cmd.Parameters.AddWithValue("@raw", seg.RawTranscript);
            cmd.Parameters.AddWithValue("@logp", seg.AvgLogProb);
            cmd.Parameters.AddWithValue("@comp", seg.CompressionRatio);
            cmd.Parameters.AddWithValue("@flag", seg.IsFlaggedForReview ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        // 3. Insert or replace sentences
        foreach (var snt in sentences)
        {
            var sntSql = @"
                INSERT OR REPLACE INTO CleanSentences (
                    SentenceId, ParentSegmentId, AnchorTimestamp, SpeakerLabel,
                    CleanedText, UserEditedText, DisplayOrder
                ) VALUES (
                    @id, @parent, @ts, @spk, @clean, @edit, @order
                );
            ";
            using var cmd = new SQLiteCommand(sntSql, conn, tx);
            cmd.Parameters.AddWithValue("@id", snt.SentenceId);
            cmd.Parameters.AddWithValue("@parent", snt.ParentSegmentId);
            cmd.Parameters.AddWithValue("@ts", snt.AnchorTimestamp);
            cmd.Parameters.AddWithValue("@spk", snt.SpeakerLabel);
            cmd.Parameters.AddWithValue("@clean", snt.CleanedText);
            cmd.Parameters.AddWithValue("@edit", (object?)snt.UserEditedText ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@order", snt.DisplayOrder);
            await cmd.ExecuteNonQueryAsync();
        }

        tx.Commit();
    }

    public static async Task<(ProjectMetadata Metadata, List<AcousticSegment> Segments, List<CleanSentence> Sentences)> LoadProjectAsync(string projectDbPath)
    {
        if (!File.Exists(projectDbPath))
            throw new FileNotFoundException($"Project file not found: {projectDbPath}");

        var meta = new ProjectMetadata();
        var segments = new List<AcousticSegment>();
        var sentences = new List<CleanSentence>();

        using var conn = new SQLiteConnection($"Data Source={projectDbPath};Version=3;");
        await conn.OpenAsync();

        // 1. Read Metadata
        using (var cmd = new SQLiteCommand("SELECT * FROM ProjectMetadata LIMIT 1;", conn))
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                meta.ProjectId = reader["ProjectId"].ToString() ?? "";
                meta.ProjectName = reader["ProjectName"].ToString() ?? "";
                meta.SourceMediaFilePath = reader["SourceMediaFilePath"].ToString() ?? "";
                meta.ExtractedAudioFilePath = reader["ExtractedAudioFilePath"].ToString() ?? "";
                meta.MediaDurationSeconds = Convert.ToDouble(reader["MediaDurationSeconds"]);
                meta.CreatedDate = DateTime.Parse(reader["CreatedDate"].ToString() ?? DateTime.UtcNow.ToString("o"));
                meta.LastModifiedDate = DateTime.Parse(reader["LastModifiedDate"].ToString() ?? DateTime.UtcNow.ToString("o"));
                meta.Status = reader["Status"].ToString() ?? "Draft";
            }
        }

        // 2. Read Segments
        using (var cmd = new SQLiteCommand("SELECT * FROM AcousticSegments ORDER BY StartTimeSeconds ASC;", conn))
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                segments.Add(new AcousticSegment
                {
                    SegmentId = reader["SegmentId"].ToString() ?? "",
                    ChunkIndex = Convert.ToInt32(reader["ChunkIndex"]),
                    StartTimeSeconds = Convert.ToDouble(reader["StartTimeSeconds"]),
                    EndTimeSeconds = Convert.ToDouble(reader["EndTimeSeconds"]),
                    RawTranscript = reader["RawTranscript"].ToString() ?? "",
                    AvgLogProb = Convert.ToDouble(reader["AvgLogProb"]),
                    CompressionRatio = Convert.ToDouble(reader["CompressionRatio"]),
                    IsFlaggedForReview = Convert.ToInt32(reader["IsFlaggedForReview"]) == 1
                });
            }
        }

        // 3. Read Sentences
        using (var cmd = new SQLiteCommand("SELECT * FROM CleanSentences ORDER BY DisplayOrder ASC;", conn))
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                sentences.Add(new CleanSentence
                {
                    SentenceId = reader["SentenceId"].ToString() ?? "",
                    ParentSegmentId = reader["ParentSegmentId"].ToString() ?? "",
                    AnchorTimestamp = Convert.ToDouble(reader["AnchorTimestamp"]),
                    SpeakerLabel = reader["SpeakerLabel"].ToString() ?? "Speaker 1",
                    CleanedText = reader["CleanedText"].ToString() ?? "",
                    UserEditedText = reader["UserEditedText"] is DBNull ? null : reader["UserEditedText"].ToString(),
                    DisplayOrder = Convert.ToInt32(reader["DisplayOrder"])
                });
            }
        }

        return (meta, segments, sentences);
    }
}
