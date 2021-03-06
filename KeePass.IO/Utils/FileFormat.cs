﻿using System;
using System.IO;

namespace KeePass.IO.Utils
{
    public static class FileFormat
    {
        private const ulong SIGNATURE = 0xb54bfb679aa2d903;

        public static bool Verify(Stream stream)
        {
            var reader = new BinaryReader(stream);

            return Sign(reader) &&
                Version(reader);
        }

        private static bool Sign(BinaryReader reader)
        {
            var signature = reader.ReadUInt64();
            return signature == SIGNATURE;
        }

        private static bool Version(BinaryReader reader)
        {
            reader.ReadInt16(); // minor
            var major = reader.ReadInt16();

            return major == 2 || major == 3;
        }
    }
}