// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Duplicati.Library.Main.Database;

/// <summary>
/// Encapsulates the logic for calculating change statistics
/// </summary>
public static class ChangeStatistics
{
    /// <summary>
    /// The tag used for log messages
    /// </summary>
    private static readonly string LOGTAG = Logging.Log.LogTagFromType(typeof(ChangeStatistics));

    /// <summary>
    /// Calculates the change statistics for the current and previous fileset.
    /// </summary>
    /// <param name="cmd">The database command to use.</param>
    /// <param name="results">The results object to update with the statistics.</param>
    /// <param name="currentFilesetId">The ID of the current fileset.</param>
    /// <param name="previousFilesetId">The ID of the previous fileset.</param>
    /// <param name="token">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task that completes when the statistics are updated.</returns>
    internal static async Task UpdateChangeStatistics(SqliteCommand cmd, BackupResults results, long currentFilesetId, long previousFilesetId, CancellationToken token)
    {
        var tmpName = $"TmpFileState_{Guid.NewGuid():N}";

        try
        {
            // Create temp table
            await cmd.SetCommandAndParameters($@"
                CREATE TEMP TABLE ""{tmpName}"" AS
                SELECT
                    ""FL"".""PrefixID"",
                    ""FL"".""Path"",
                    ""FL"".""BlocksetID"",
                    ""BS_Meta"".""Fullhash"" AS ""Metahash"",
                    CASE ""FE"".""FilesetID""
                        WHEN @LastFilesetId THEN 0
                        WHEN @CurrentFilesetId THEN 1
                    END AS ""Source""
                FROM ""FileLookup"" ""FL""
                JOIN ""FilesetEntry"" ""FE""
                    ON ""FL"".""ID"" = ""FE"".""FileID""
                LEFT JOIN ""Blockset"" ""BS_Data""
                    ON ""FL"".""BlocksetID"" = ""BS_Data"".""ID""
                LEFT JOIN ""Metadataset"" M
                    ON ""FL"".""MetadataID"" = M.""ID""
                LEFT JOIN ""Blockset"" ""BS_Meta""
                    ON M.""BlocksetID"" = ""BS_Meta"".""ID""
                WHERE ""FE"".""FilesetID"" IN (
                    @LastFilesetId,
                    @CurrentFilesetId
                );
            ")
                .SetParameterValue("@LastFilesetId", previousFilesetId)
                .SetParameterValue("@CurrentFilesetId", currentFilesetId)
                .ExecuteNonQueryAsync(token)
                .ConfigureAwait(false);

            // Index for fast comparison
            await cmd.ExecuteNonQueryAsync($@"
                CREATE INDEX ""idx_{tmpName}""
                ON ""{tmpName}"" (
                    ""PrefixID"",
                    ""Path""
                )
            ", token)
                .ConfigureAwait(false);

            // Added
            results.AddedFolders = await CountAdded(cmd, tmpName, LocalDatabase.FOLDER_BLOCKSET_ID, null, token)
                .ConfigureAwait(false);
            results.AddedSymlinks = await CountAdded(cmd, tmpName, LocalDatabase.SYMLINK_BLOCKSET_ID, null, token)
                .ConfigureAwait(false);
            results.AddedFiles = await CountAdded(cmd, tmpName, null, [
                LocalDatabase.FOLDER_BLOCKSET_ID,
                LocalDatabase.SYMLINK_BLOCKSET_ID
            ], token)
                .ConfigureAwait(false);

            // Deleted
            results.DeletedFolders = await CountDeleted(cmd, tmpName, LocalDatabase.FOLDER_BLOCKSET_ID, null, token)
                .ConfigureAwait(false);
            results.DeletedSymlinks = await CountDeleted(cmd, tmpName, LocalDatabase.SYMLINK_BLOCKSET_ID, null, token)
                .ConfigureAwait(false);
            results.DeletedFiles = await CountDeleted(cmd, tmpName, null, [
                LocalDatabase.FOLDER_BLOCKSET_ID,
                LocalDatabase.SYMLINK_BLOCKSET_ID
            ], token)
                .ConfigureAwait(false);

            // Modified
            results.ModifiedFolders = await CountModified(cmd, tmpName, LocalDatabase.FOLDER_BLOCKSET_ID, null, token)
                .ConfigureAwait(false);
            results.ModifiedSymlinks = await CountModified(cmd, tmpName, LocalDatabase.SYMLINK_BLOCKSET_ID, null, token)
                .ConfigureAwait(false);
            results.ModifiedFiles = await CountModified(cmd, tmpName, null, [
                LocalDatabase.FOLDER_BLOCKSET_ID,
                LocalDatabase.SYMLINK_BLOCKSET_ID
            ], token)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await cmd.ExecuteNonQueryAsync($@"DROP TABLE IF EXISTS ""{tmpName}"";", token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.Log.WriteWarningMessage(LOGTAG, "DropTemp", ex, $"Failed to drop {tmpName}");
            }
        }
    }

    /// <summary>
    /// Counts the number of added files or folders.
    /// </summary>
    /// <param name="cmd">The database command to use.</param>
    /// <param name="tmpName">The name of the temporary table.</param>
    /// <param name="blocksetId">The ID of the blockset to count, or null for all.</param>
    /// <param name="excludeBlocksets">The blocksets to exclude from the count.</param>
    /// <param name="token">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task that when awaited contains the number of added files or folders.</returns>
    private static async Task<long> CountAdded(SqliteCommand cmd, string tmpName, long? blocksetId, long[]? excludeBlocksets, CancellationToken token)
    {
        var conditions = $@"
            ""Source"" = 1
            AND NOT EXISTS (
                SELECT 1
                FROM ""{tmpName}"" ""B""
                WHERE
                    ""B"".""Source"" = 0
                    AND ""B"".""PrefixID"" = ""A"".""PrefixID""
                    AND ""B"".""Path"" = ""A"".""Path""
            )
        ";

        return await CountWithCondition(cmd, tmpName, "A", conditions, blocksetId, excludeBlocksets, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the number of deleted files or folders.
    /// </summary>
    /// <param name="cmd">The database command to use.</param>
    /// <param name="tmpName">The name of the temporary table.</param>
    /// <param name="blocksetId">The ID of the blockset to count, or null for all.</param>
    /// <param name="excludeBlocksets">The blocksets to exclude from the count.</param>
    /// <param name="token">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task that when awaited contains the number of deleted files or folders.</returns>
    private static async Task<long> CountDeleted(SqliteCommand cmd, string tmpName, long? blocksetId, long[]? excludeBlocksets, CancellationToken token)
    {
        var conditions = @$"
            ""Source"" = 0
            AND NOT EXISTS (
                SELECT 1
                FROM ""{tmpName}"" ""B""
                WHERE
                    ""B"".""Source"" = 1
                    AND ""B"".""PrefixID"" = ""A"".""PrefixID""
                    AND ""B"".""Path"" = ""A"".""Path""
            )
        ";

        return await CountWithCondition(cmd, tmpName, "A", conditions, blocksetId, excludeBlocksets, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the number of modified files or folders.
    /// </summary>
    /// <param name="cmd">The database command to use.</param>
    /// <param name="tmpName">The name of the temporary table.</param>
    /// <param name="blocksetId">The ID of the blockset to count, or null for all.</param>
    /// <param name="excludeBlocksets">The blocksets to exclude from the count.</param>
    /// <param name="token">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task that when awaited contains the number of modified files or folders.</returns>
    private static async Task<long> CountModified(SqliteCommand cmd, string tmpName, long? blocksetId, long[]? excludeBlocksets, CancellationToken token)
    {
        string conditions;

        if (blocksetId == LocalDatabase.FOLDER_BLOCKSET_ID || blocksetId == LocalDatabase.SYMLINK_BLOCKSET_ID)
        {
            conditions = @"
                ""A"".""Source"" = 0
                AND ""B"".""Source"" = 1
                AND ""A"".""PrefixID"" = ""B"".""PrefixID""
                AND ""A"".""Path"" = ""B"".""Path""
                AND ""A"".""Metahash"" IS NOT ""B"".""Metahash""
            ";
        }
        else
        {
            conditions = @"
                ""A"".""Source"" = 0
                AND ""B"".""Source"" = 1
                AND ""A"".""PrefixID"" = ""B"".""PrefixID""
                AND ""A"".""Path"" = ""B"".""Path""
                AND (
                    ""A"".""BlocksetID"" IS NOT ""B"".""BlocksetID""
                    OR ""A"".""Metahash"" IS NOT ""B"".""Metahash""
                )
            ";
        }

        string blocksetCondition = GetBlocksetCondition("A", blocksetId, excludeBlocksets);
        if (!string.IsNullOrEmpty(blocksetCondition))
            conditions += $" AND {blocksetCondition}";

        string sql = $@"
            SELECT COUNT(*)
            FROM ""{tmpName}"" ""A""
            JOIN ""{tmpName}"" ""B""
                ON ""A"".""PrefixID"" = ""B"".""PrefixID""
                AND ""A"".""Path"" = ""B"".""Path""
            WHERE {conditions}
        ";

        return await cmd
            .SetCommandAndParameters(sql)
            .ExecuteScalarInt64Async(0, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the number of files or folders with a specific condition.
    /// </summary>
    /// <param name="cmd">The database command to use.</param>
    /// <param name="tmpName">The name of the temporary table.</param>
    /// <param name="alias">The alias for the temporary table.</param>
    /// <param name="baseCondition">The base condition for the count.</param>
    /// <param name="blocksetId">The ID of the blockset to count, or null for all.</param>
    /// <param name="excludeBlocksets">The blocksets to exclude from the count.</param>
    /// <param name="token">The cancellation token to monitor for cancellation requests.</param>
    /// <returns>A task that when awaited contains the number of files or folders that match the condition.</returns>
    private static async Task<long> CountWithCondition(SqliteCommand cmd, string tmpName, string alias, string baseCondition, long? blocksetId, long[]? excludeBlocksets, CancellationToken token)
    {
        var fullCondition = baseCondition;
        var blocksetCondition = GetBlocksetCondition(alias, blocksetId, excludeBlocksets);
        if (!string.IsNullOrEmpty(blocksetCondition))
            fullCondition += " AND " + blocksetCondition;

        var sql = $@"
            SELECT COUNT(*)
            FROM ""{tmpName}"" ""{alias}""
            WHERE {fullCondition}
        ";

        return await cmd
            .SetCommandAndParameters(sql)
            .ExecuteScalarInt64Async(0, token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Generates the blockset condition for the SQL query
    /// </summary>
    /// <param name="alias">The alias for the temporary table</param>
    /// <param name="blocksetId">The ID of the blockset to count, or null for all</param>
    /// <param name="exclude">The blocksets to exclude from the count</param>
    /// <returns>>The blockset condition for the SQL query</returns>
    private static string GetBlocksetCondition(string alias, long? blocksetId, long[]? exclude)
    {
        if (blocksetId.HasValue)
            return @$"{alias}.""BlocksetID"" = {blocksetId.Value}";
        if (exclude?.Length > 0)
            return @$"{alias}.""BlocksetID"" NOT IN ({string.Join(",", exclude)})";
        return string.Empty;
    }
}