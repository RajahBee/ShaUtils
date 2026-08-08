using System;
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

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            long fileLength = stream.Length;

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, token)) > 0)
            {
                crc = Calculate(buffer, 0, bytesRead, crc);
                totalBytesRead += bytesRead;

                if (progress != null && slotIndex >= 0)
                {
                    int percentage = fileLength > 0 ? (int)((double)totalBytesRead / fileLength * 100) : 100;
                    progress.Report(new ProgressReport
                    {
                        Type = ProgressReport.ReportType.SlotUpdate,
                        UpdateType = ProgressReport.SlotUpdateType.InProgress,
                        SlotIndex = slotIndex,
                        ProgressPercentage = percentage,
                        StatusText = $"{percentage}%"
                    });
                }
            }

            return crc;
        }
    }
}
