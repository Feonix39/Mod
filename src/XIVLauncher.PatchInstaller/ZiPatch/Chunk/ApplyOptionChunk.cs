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
    /// An "APLY" (Apply Option) chunk.
    /// </summary>
    public class ApplyOptionChunk : ZiPatchChunk
    {
        /// <summary>
        /// The chunk type.
        /// </summary>
        public new static string Type = "APLY";

        /// <summary>
        /// ApplyOption kinds.
        /// </summary>
        public enum ApplyOptionKind : uint
        {
            /// <summary>
            /// Ignore missing.
            /// </summary>
            IgnoreMissing = 1,

            /// <summary>
            /// Ignore old mismatch.
            /// </summary>
            IgnoreOldMismatch = 2,
        }

        /// <summary>
        /// Gets the option kind.
        /// </summary>
        // These are both false on all files seen
        public ApplyOptionKind OptionKind { get; protected set; }

        /// <summary>
        /// Gets a value indicating whether the option should be enabled.
        /// </summary>
        public bool OptionValue { get; protected set; }

        public ApplyOptionChunk(ChecksumBinaryReader reader, int size) : base(reader, size)
        {}

        protected override void ReadChunk()
        {
            var start = reader.BaseStream.Position;

            OptionKind = (ApplyOptionKind) reader.ReadUInt32BE();

            // Discarded padding, always 0x0000_0004 as far as observed
            reader.ReadBytes(4);

            var value = reader.ReadUInt32BE() != 0;

            if (OptionKind == ApplyOptionKind.IgnoreMissing ||
                OptionKind == ApplyOptionKind.IgnoreOldMismatch)
                OptionValue = value;
            else
                OptionValue = false; // defaults to false if OptionKind isn't valid

            reader.ReadBytes(Size - (int)(reader.BaseStream.Position - start));
        }

        public override void ApplyChunk(ZiPatchConfig config)
        {
            switch (OptionKind)
            {
                case ApplyOptionKind.IgnoreMissing:
                    config.IgnoreMissing = OptionValue;
                    break;
                case ApplyOptionKind.IgnoreOldMismatch:
                    config.IgnoreOldMismatch = OptionValue;
                    break;
            }
        }

        public override string ToString()
        {
            return $"{Type}:{OptionKind}:{OptionValue}";
        }
    }
}
