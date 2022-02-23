using XIVLauncher.Common.Patching.Util;

namespace XIVLauncher.Common.Patching.ZiPatch.Chunk
{
    public class ApplyOptionChunk : ZiPatchChunk
    {
        public new static string Type = "APLY";

        public enum ApplyOptionKind : uint
        {
            IgnoreMissing = 1,
            IgnoreOldMismatch = 2
        }

        // These are both false on all files seen
        public ApplyOptionKind OptionKind { get; protected set; }

        public bool OptionValue { get; protected set; }

        public ApplyOptionChunk(ChecksumBinaryReader reader, int offset, int size) : base(reader, offset, size) { }

        protected override void ReadChunk()
        {
            var start = this.Reader.BaseStream.Position;

            OptionKind = (ApplyOptionKind)reader.ReadUInt32BE();

            // Discarded padding, always 0x0000_0004 as far as observed
            this.Reader.ReadBytes(4);

            var value = this.Reader.ReadUInt32BE() != 0;

            if (OptionKind == ApplyOptionKind.IgnoreMissing ||
                OptionKind == ApplyOptionKind.IgnoreOldMismatch)
                OptionValue = value;
            else
                OptionValue = false; // defaults to false if OptionKind isn't valid

            this.Reader.ReadBytes(Size - (int)(this.Reader.BaseStream.Position - start));
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
