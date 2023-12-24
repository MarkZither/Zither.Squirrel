using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace Squirrel.Packaging
{
    public class FileComparer
    {
        /// <summary>
        /// Fileinfo for source file
        /// </summary>
        protected readonly FileInfo FileInfo1;

        /// <summary>
        /// Fileinfo for target file
        /// </summary>
        protected readonly FileInfo FileInfo2;

        /// <summary>
        /// Base class for creating a file comparer
        /// </summary>
        /// <param name="filePath01">Absolute path to source file</param>
        /// <param name="filePath02">Absolute path to target file</param>
        public FileComparer(string filePath01, string filePath02)
        {
            FileInfo1 = new FileInfo(filePath01);
            FileInfo2 = new FileInfo(filePath02);
            EnsureFilesExist();
        }

        const int FS_BUFFER_SIZE = 1000000; // 1mb
        const int BUFFER_SIZE = 4096;
        const int EQMASK = unchecked((int) (0b1111_1111_1111_1111_1111_1111_1111_1111));
        const int AvxRegisterSize = 32;
        const int SseRegisterSize = 16;

        /// <summary>
        /// Compares the two given files and returns true if the files are the same
        /// </summary>
        /// <returns>true if the files are the same, false otherwise</returns>
        public unsafe bool Compare()
        {
            if (IsDifferentLength()) {
                return false;
            }

            if (IsSameFile()) {
                return true;
            }

            using var stream1 = new FileStream(FileInfo1.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FS_BUFFER_SIZE);
            using var stream2 = new FileStream(FileInfo2.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FS_BUFFER_SIZE);

            long length = FileInfo1.Length;
            long totalRead = 0;
            var buffer1 = new byte[BUFFER_SIZE];
            var buffer2 = new byte[BUFFER_SIZE];
            fixed (byte* oh1 = buffer1)
            fixed (byte* oh2 = buffer2) {
                while (totalRead < length) {
                    var count1 = ReadIntoBuffer(stream1, buffer1);
                    var count2 = ReadIntoBuffer(stream2, buffer2);
                    if (count1 != count2) {
                        return false;
                    }
                    if (count1 == 0) {
                        return true;
                    }
                    if (!BlockContentsMatch(oh1, oh2, count1)) {
                        return false;
                    }
                    totalRead += count1;
                }
            }
            return true;
        }

        protected int ReadIntoBuffer(in Stream stream, in byte[] buffer)
        {
            var bytesRead = 0;
            while (bytesRead < buffer.Length) {
                var read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                // Reached end of stream.
                if (read == 0) {
                    return bytesRead;
                }
                bytesRead += read;
            }
            return bytesRead;
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool BlockContentsMatch(byte* sourcePtr, byte* targetPtr, int length)
        {
            int sOffset = 0;
            int tOffset = 0;
            byte* sPtr = sourcePtr;
            byte* tPtr = targetPtr;
            int yetToExamine = length;

            if (Avx2.IsSupported && yetToExamine >= AvxRegisterSize) {
                while (yetToExamine >= AvxRegisterSize) {
                    Vector256<byte> lv = Avx.LoadVector256(&sPtr[sOffset]);
                    Vector256<byte> rv = Avx.LoadVector256(&tPtr[tOffset]);
                    if (Avx2.MoveMask(Avx2.CompareEqual(lv, rv)) != EQMASK)
                        return false;

                    sOffset += AvxRegisterSize;
                    tOffset += AvxRegisterSize;
                    yetToExamine -= AvxRegisterSize;
                }
            }

            if (Sse2.IsSupported && yetToExamine >= SseRegisterSize) {
                while (yetToExamine >= SseRegisterSize) {
                    Vector128<byte> lv = Sse2.LoadVector128(&sPtr[sOffset]);
                    Vector128<byte> rv = Sse2.LoadVector128(&tPtr[tOffset]);
                    if ((uint) Sse2.MoveMask(Sse2.CompareEqual(lv, rv)) != ushort.MaxValue)
                        return false;

                    sOffset += SseRegisterSize;
                    tOffset += SseRegisterSize;
                    yetToExamine -= SseRegisterSize;
                }
            }

            int vectorSize = Vector<byte>.Count;
            if (yetToExamine >= vectorSize) {
                var sBuf = new Span<byte>(sourcePtr, length);
                var tBuf = new Span<byte>(targetPtr, length);

                while (yetToExamine >= vectorSize) {
                    Vector<byte> lv = new Vector<byte>(sBuf.Slice(sOffset));
                    Vector<byte> rv = new Vector<byte>(tBuf.Slice(tOffset));
                    if (!Vector.EqualsAll(lv, rv))
                        return false;

                    sOffset += vectorSize;
                    tOffset += vectorSize;
                    yetToExamine -= vectorSize;
                }
            }

            while (yetToExamine > 0) {
                if (sPtr[sOffset] != tPtr[tOffset])
                    return false;

                --yetToExamine;
                ++sOffset;
                ++tOffset;
            }

            return true;
        }

        private bool IsSameFile()
        {
            return string.Equals(FileInfo1.FullName, FileInfo2.FullName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Does an early comparison by checking files Length, if lengths are not the same, files are definetely different
        /// </summary>
        /// <returns>true if different length</returns>
        private bool IsDifferentLength()
        {
            return FileInfo1.Length != FileInfo2.Length;
        }

        /// <summary>
        /// Makes sure files exist
        /// </summary>
        private void EnsureFilesExist()
        {
            if (FileInfo1.Exists == false) {
                throw new ArgumentNullException(nameof(FileInfo1));
            }
            if (FileInfo2.Exists == false) {
                throw new ArgumentNullException(nameof(FileInfo2));
            }
        }
    }
}
