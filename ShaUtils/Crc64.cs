using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ShaUtils
{
    public static class Crc64
    {
        private const ulong Polynomial = 0x923282036A7F594B; // Reflected polynomial for ECMA-182
        private static readonly ulong[] Table = new ulong[256];

        static Crc64()
        {
            for (uint i = 0; i < 256; i++)
            {
                ulong crc = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 1) != 0)
                    {
                        crc = (crc >> 1) ^ Polynomial;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
                Table[i] = crc;
            }
        }

        public static ulong Calculate(byte[] buffer, int offset, int count, ulong seed = 0)
        {
            ulong crc = ~seed;
            for (int i = 0; i < count; i++)
            {
                byte index = (byte)((crc ^ buffer[offset + i]) & 0xFF);
                crc = (crc >> 8) ^ Table[index];
            }
            return ~crc;
        }

        public static async Task<ulong> CalculateAsync(string filePath, CancellationToken token, IProgress<ProgressReport>? progress = null, int slotIndex = -1)
        {
            const int bufferSize = 1024 * 64; // 64KB buffer
            var buffer = new byte[bufferSize];
            ulong crc = 0;
            long totalBytesRead = 0;
            var stopwatch = new Stopwatch();
            var lastReportTime = DateTime.MinValue;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            long fileLength = stream.Length;

            int bytesRead;
            stopwatch.Start();
            while ((bytesRead = await stream.ReadAsync(buffer, token)) > 0)
            {
                crc = Calculate(buffer, 0, bytesRead, crc);
                totalBytesRead += bytesRead;

                if (progress != null && slotIndex >= 0 && (DateTime.UtcNow - lastReportTime > TimeSpan.FromMilliseconds(250)))
                {
                    var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        var speed = (long)(totalBytesRead / elapsedSeconds);
                        var remainingBytes = fileLength - totalBytesRead;
                        var estimatedSecondsRemaining = speed > 0 ? (double)remainingBytes / speed : 0;
                        var percentage = (int)((double)totalBytesRead / fileLength * 100);

                        string statusText = $"{MainWindow.FormatFileSize(speed)}/s";
                        if (estimatedSecondsRemaining > 3)
                        {
                            statusText += $" {MainWindow.FormatTimeSpan(TimeSpan.FromSeconds(estimatedSecondsRemaining))}";
                        }

                        progress.Report(new ProgressReport
                        {
                            Type = ProgressReport.ReportType.SlotUpdate,
                            UpdateType = ProgressReport.SlotUpdateType.InProgress,
                            SlotIndex = slotIndex,
                            ProgressPercentage = percentage,
                            StatusText = statusText
                        });
                        lastReportTime = DateTime.UtcNow;
                    }
                }
            }

            return crc;
        }
    }
}
