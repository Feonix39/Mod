using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.PatchInstaller.Util;

namespace XIVLauncher.PatchInstaller.ZiPatch.Chunk
{
    /// <summary>
    /// A "DELD" (Delete Directory) chunk.
    /// </summary>
    public class DeleteDirectoryChunk : ZiPatchChunk
    {
        /// <summary>
        /// The chunk type.
        /// </summary>
        public new static string Type = "DELD";

        public string DirName { get; protected set; }


        public DeleteDirectoryChunk(ChecksumBinaryReader reader, int size) : base(reader, size)
        {}

        protected override void ReadChunk()
        {
            var start = reader.BaseStream.Position;

            var dirNameLen = reader.ReadUInt32BE();

            DirName = reader.ReadFixedLengthString(dirNameLen);

            reader.ReadBytes(Size - (int)(reader.BaseStream.Position - start));
        }

        /// <inheritdoc/>
        public override void ApplyChunk(ZiPatchConfig config)
        {
            try
            {
                Directory.Delete(config.GamePath + DirName);
            }
            catch (Exception e)
            {
                Log.Debug(e, $"Ran into {this}, failed at deleting the dir.");
                throw;
            }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return $"{Type}:{DirName}";
        }
    }
}
