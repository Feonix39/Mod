using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XIVLauncher.PatchInstaller.Util;

namespace XIVLauncher.PatchInstaller.ZiPatch.Chunk
{
    /// <summary>
    /// An "EOF_" (End of File) chunk.
    /// </summary>
    public class EndOfFileChunk : ZiPatchChunk
    {
        /// <summary>
        /// The chunk type.
        /// </summary>
        public new static string Type = "EOF_";

        protected override void ReadChunk()
        {
            var start = reader.BaseStream.Position;

            reader.ReadBytes(Size - (int)(reader.BaseStream.Position - start));
        }


        public EndOfFileChunk(ChecksumBinaryReader reader, int size) : base(reader, size)
        {}

        public override string ToString()
        {
            return Type;
        }
    }
}
